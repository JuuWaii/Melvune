using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FnMusic.Core;

namespace FnMusic.Infrastructure;

public sealed record NavidromeConnection(Guid SourceId, string Name, string ServerAddress, string Username)
{
    // Missing provider fields in existing version 1 indexes retain their Navidrome identity.
    public string Provider { get; init; } = "navidrome";
}

/// <summary>
/// Stores connection descriptions separately from credentials delegated to the session vault.
/// A successful atomic index replacement is the commit point. Before that point, failures leave
/// the previous connection and credentials unchanged. Cleanup after commit is best effort and
/// never turns a committed save or removal into an apparent failure.
/// </summary>
public sealed class NavidromeConnectionStore(string directory, ISessionVault vault)
{
    private readonly string file = Path.Combine(directory, "navidrome-connections.json");
    private sealed record Entry(NavidromeConnection Connection, Guid CredentialId);
    private sealed record Document(int Version, Entry[] Connections);
    private const int MaximumConnections = 100;
    private const int MaximumDocumentBytes = 1024 * 1024;

    public async Task<IReadOnlyList<NavidromeConnection>> LoadAsync(CancellationToken ct = default) =>
        (await LoadEntriesAsync(ct)).Select(entry => entry.Connection).ToArray();

    public async Task<NavidromeCredentials?> LoadCredentialsAsync(Guid sourceId, CancellationToken ct = default)
    {
        if (sourceId == Guid.Empty) throw new ArgumentException("Invalid source identity", nameof(sourceId));
        var entry = (await LoadEntriesAsync(ct)).SingleOrDefault(item => item.Connection.SourceId == sourceId);
        if (entry is null) return null;
        var text = await vault.LoadAsync(CredentialKey(entry), ct);
        if (text is null) return null;
        if (text.Length > 4096) throw new InvalidDataException("Invalid saved credentials");
        var credentials = JsonSerializer.Deserialize<NavidromeCredentials>(text)
            ?? throw new InvalidDataException("Invalid saved credentials");
        ValidateCredentials(entry.Connection, credentials);
        return credentials;
    }

    public async Task SaveAsync(NavidromeConnection connection, NavidromeCredentials credentials, CancellationToken ct = default)
    {
        connection = NormalizeConnection(connection);
        ValidateCredentials(connection, credentials);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var writer = await LockAsync(timeout.Token);
        var entries = (await LoadEntriesAsync(ct)).ToList();
        var previous = entries.SingleOrDefault(item => item.Connection.SourceId == connection.SourceId);
        if (previous is not null && (!SameAccount(previous.Connection, connection) ||
            previous.Connection.Provider != connection.Provider))
            throw new InvalidOperationException("A different server, account or provider requires a new source identity.");
        if (entries.Any(item => item.Connection.SourceId != connection.SourceId && SameAccount(item.Connection, connection)))
            throw new InvalidOperationException("This account already has a connection.");
        if (previous is null && entries.Count >= MaximumConnections)
            throw new InvalidOperationException("Too many music connections.");
        var next = new Entry(connection, Guid.NewGuid());
        if (previous is not null) entries.Remove(previous);
        entries.Add(next);
        bool committed = false;
        try
        {
            await vault.SaveAsync(CredentialKey(next), JsonSerializer.Serialize(credentials), ct);
            await CommitAsync(entries, ct);
            committed = true;
        }
        finally
        {
            // A cancelled or failed vault write may already have created its new slot.
            if (!committed) await TryClearCredentialsAsync(next);
        }
        if (previous is not null) await TryClearCredentialsAsync(previous);
    }

    public async Task RemoveAsync(Guid sourceId, CancellationToken ct = default)
    {
        if (sourceId == Guid.Empty) throw new ArgumentException("Invalid source identity", nameof(sourceId));
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var writer = await LockAsync(timeout.Token);
        var entries = (await LoadEntriesAsync(ct)).ToList();
        var previous = entries.SingleOrDefault(item => item.Connection.SourceId == sourceId);
        if (previous is null) return;
        entries.Remove(previous);
        await CommitAsync(entries, ct);
        await TryClearCredentialsAsync(previous);
    }

    public static string NormalizeServerAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 2048 || address.Any(char.IsControl) ||
            !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Invalid music server address", nameof(address));
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    public static NavidromeConnection NormalizeConnection(NavidromeConnection connection)
    {
        if (connection is null || connection.SourceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(connection.Name) || connection.Name.Length > 120 || connection.Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(connection.Username) || connection.Username.Length > 256 || connection.Username.Any(char.IsControl))
            throw new ArgumentException("Invalid music connection", nameof(connection));
        return connection with { Name = connection.Name.Trim(), ServerAddress = NormalizeServerAddress(connection.ServerAddress),
            Username = connection.Username.Trim(), Provider = NormalizeProvider(connection.Provider) };
    }

    internal static string NormalizeProvider(string provider) => provider is "navidrome" or "subsonic"
        ? provider : throw new ArgumentException("Invalid music provider", nameof(provider));

    private static void ValidateCredentials(NavidromeConnection connection, NavidromeCredentials credentials)
    {
        if (credentials is null || !string.Equals(connection.Username, credentials.Username, StringComparison.Ordinal) ||
            credentials.Token is not { Length: 32 } || !credentials.Token.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(credentials.Salt) || credentials.Salt.Length > 128 || credentials.Salt.Any(char.IsControl))
            throw new InvalidDataException("Invalid saved credentials");
    }

    private static bool SameAccount(NavidromeConnection first, NavidromeConnection second) =>
        string.Equals(first.ServerAddress, second.ServerAddress, StringComparison.Ordinal) &&
        string.Equals(first.Username, second.Username, StringComparison.Ordinal);

    private static string CredentialKey(Entry entry) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"fnmusic:navidrome:{entry.Connection.SourceId:N}:{entry.CredentialId:N}")));

    private async Task<Entry[]> LoadEntriesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(file)) return [];
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > MaximumDocumentBytes) throw new InvalidDataException("Invalid music connections");
        var document = await JsonSerializer.DeserializeAsync<Document>(input, cancellationToken: ct);
        if (document is null || document.Version != 1 || document.Connections is null || document.Connections.Length > MaximumConnections)
            throw new InvalidDataException("Invalid music connections");
        var identities = new HashSet<Guid>();
        var accounts = new HashSet<(string Address, string Username)>();
        var normalized = new List<Entry>();
        foreach (var item in document.Connections)
        {
            if (item is null || item.CredentialId == Guid.Empty) throw new InvalidDataException("Invalid music connections");
            NavidromeConnection connection;
            try { connection = NormalizeConnection(item.Connection); }
            catch (ArgumentException) { throw new InvalidDataException("Invalid music connections"); }
            if (!identities.Add(connection.SourceId) || !accounts.Add((connection.ServerAddress, connection.Username)))
                throw new InvalidDataException("Invalid music connections");
            normalized.Add(item with { Connection = connection });
        }
        return normalized.ToArray();
    }

    private async Task CommitAsync(List<Entry> entries, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, entries.ToArray()));
        if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("Invalid music connections");
        string pending = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(pending, bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(pending, file, true);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(50, ct); }
        }
    }

    private async Task TryClearCredentialsAsync(Entry entry)
    {
        try { await vault.ClearAsync(CredentialKey(entry), CancellationToken.None); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (CryptographicException) { }
    }
}
