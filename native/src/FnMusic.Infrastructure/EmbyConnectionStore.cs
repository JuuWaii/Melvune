using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FnMusic.Core;

namespace FnMusic.Infrastructure;

public sealed record EmbyConnection(Guid SourceId, string Name, string ServerAddress, string Username, string UserId);

/// <summary>
/// Stores connection descriptions separately from credentials delegated to the session vault.
/// A successful atomic index replacement is the commit point. Before that point, failures leave
/// the previous connection and credentials unchanged. Cleanup after commit is best effort and
/// never turns a committed save or removal into an apparent failure.
/// </summary>
public sealed class EmbyConnectionStore(string directory, ISessionVault vault)
{
    private readonly string file = Path.Combine(directory, "emby-connections.json");
    private sealed record Entry(EmbyConnection Connection, Guid CredentialId);
    private sealed record Document(int Version, Entry[] Connections);
    private const int MaximumConnections = 100;
    private const int MaximumDocumentBytes = 1024 * 1024;

    public async Task<IReadOnlyList<EmbyConnection>> LoadAsync(CancellationToken ct = default) =>
        (await LoadEntriesAsync(ct)).Select(entry => entry.Connection).ToArray();

    public async Task<EmbyCredentials?> LoadCredentialsAsync(Guid sourceId, CancellationToken ct = default)
    {
        if (sourceId == Guid.Empty) throw new ArgumentException("Invalid source identity", nameof(sourceId));
        var entry = (await LoadEntriesAsync(ct)).SingleOrDefault(item => item.Connection.SourceId == sourceId);
        if (entry is null) return null;
        var text = await vault.LoadAsync(CredentialKey(entry), ct);
        if (text is null) return null;
        // Serialized escaping can expand a valid opaque token to six bytes per character.
        if (text.Length > 32 * 1024) throw new InvalidDataException("Invalid saved credentials");
        var credentials = JsonSerializer.Deserialize<EmbyCredentials>(text)
            ?? throw new InvalidDataException("Invalid saved credentials");
        ValidateCredentials(entry.Connection, credentials);
        return credentials;
    }

    public async Task SaveAsync(EmbyConnection connection, EmbyCredentials credentials, CancellationToken ct = default)
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
        if (previous is not null && !SameAccount(previous.Connection, connection))
            throw new InvalidOperationException("A different server or account requires a new source identity.");
        if (entries.Any(item => item.Connection.SourceId != connection.SourceId &&
            item.Connection.ServerAddress == connection.ServerAddress && item.Connection.UserId == connection.UserId))
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
            throw new ArgumentException("Invalid Emby server address", nameof(address));
        string normalized = uri.AbsoluteUri.TrimEnd('/');
        // Accept the web server root or its API root, including reverse-proxy subpaths.
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/emby", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^5];
        return normalized + "/emby/";
    }

    public static EmbyConnection NormalizeConnection(EmbyConnection connection)
    {
        if (connection is null || connection.SourceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(connection.Name) || connection.Name.Length > 120 || connection.Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(connection.Username) || connection.Username.Length > 256 || connection.Username.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(connection.UserId) || connection.UserId.Length > 256 || connection.UserId.Any(char.IsControl) ||
            connection.UserId != connection.UserId.Trim())
            throw new ArgumentException("Invalid music connection", nameof(connection));
        return connection with { Name = connection.Name.Trim(), ServerAddress = NormalizeServerAddress(connection.ServerAddress),
            Username = connection.Username.Trim() };
    }

    private static void ValidateCredentials(EmbyConnection connection, EmbyCredentials credentials)
    {
        if (credentials is null || !string.Equals(connection.UserId, credentials.UserId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(credentials.AccessToken) || credentials.AccessToken.Length > 4096 ||
            credentials.AccessToken.Any(char.IsControl) || credentials.AccessToken != credentials.AccessToken.Trim())
            throw new InvalidDataException("Invalid saved credentials");
    }

    private static bool SameAccount(EmbyConnection first, EmbyConnection second) =>
        string.Equals(first.ServerAddress, second.ServerAddress, StringComparison.Ordinal) &&
        string.Equals(first.Username, second.Username, StringComparison.Ordinal) &&
        string.Equals(first.UserId, second.UserId, StringComparison.Ordinal);

    private static string CredentialKey(Entry entry) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"fnmusic:emby:{entry.Connection.SourceId:N}:{entry.CredentialId:N}")));

    private async Task<Entry[]> LoadEntriesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(file)) return [];
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > MaximumDocumentBytes) throw new InvalidDataException("Invalid music connections");
        // Bound bytes actually consumed, as well as the initial length, if an external writer grows the file.
        using var bounded = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumDocumentBytes + 1 - (int)bounded.Length)), ct);
            if (read == 0) break;
            bounded.Write(buffer, 0, read);
            if (bounded.Length > MaximumDocumentBytes) throw new InvalidDataException("Invalid music connections");
        }
        var document = JsonSerializer.Deserialize<Document>(bounded.GetBuffer().AsSpan(0, (int)bounded.Length));
        if (document is null || document.Version != 1 || document.Connections is null || document.Connections.Length > MaximumConnections)
            throw new InvalidDataException("Invalid music connections");
        var identities = new HashSet<Guid>();
        var accounts = new HashSet<(string Address, string Username, string UserId)>();
        var users = new HashSet<(string Address, string UserId)>();
        var normalized = new List<Entry>();
        foreach (var item in document.Connections)
        {
            if (item is null || item.CredentialId == Guid.Empty) throw new InvalidDataException("Invalid music connections");
            EmbyConnection connection;
            try { connection = NormalizeConnection(item.Connection); }
            catch (ArgumentException) { throw new InvalidDataException("Invalid music connections"); }
            if (!identities.Add(connection.SourceId) || !accounts.Add((connection.ServerAddress, connection.Username, connection.UserId)) ||
                !users.Add((connection.ServerAddress, connection.UserId)))
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
