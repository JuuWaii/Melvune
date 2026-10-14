using System.Text.Json;
using System.Text.Json.Serialization;

namespace FnMusic.Infrastructure;

public sealed record HttpJsonConnection(Guid SourceId, string Name, string ServerAddress, HttpJsonDefinition Definition);

/// <summary>
/// Persists anonymous catalog descriptions, never playback URLs or authorization data.
/// The atomic index replacement is the commit point; cancellation before it preserves the
/// previous index, and cancellation after it cannot report a committed operation as failed.
/// </summary>
public sealed class HttpJsonConnectionStore(string directory)
{
    private readonly string file = Path.Combine(directory, "http-json-connections.json");
    private sealed record Document(int Version, HttpJsonConnection[] Connections);
    private const int MaximumConnections = 100;
    private const int MaximumDocumentBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24
    };
#if DEBUG
    // A private diagnostic seam exercises cancellation on both sides of the atomic commit.
    private Action<bool>? commitBoundary = null;
#endif

    public async Task<IReadOnlyList<HttpJsonConnection>> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(file)) return [];
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > MaximumDocumentBytes) throw new InvalidDataException("Invalid HTTP JSON music connections.");
        // Bound the actual read as well as the initial file length, including external edits.
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct);
        if (await input.ReadAsync(new byte[1], ct) != 0)
            throw new InvalidDataException("Invalid HTTP JSON music connections.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        RejectDuplicateProperties(json.RootElement);
        var document = json.RootElement.Deserialize<Document>(JsonOptions);
        if (document is null || document.Version != 1 || document.Connections is null || document.Connections.Length > MaximumConnections)
            throw new InvalidDataException("Invalid HTTP JSON music connections.");
        var identities = new HashSet<Guid>();
        var endpoints = new HashSet<(string Address, HttpJsonDefinition Definition)>();
        var normalized = new List<HttpJsonConnection>();
        foreach (var item in document.Connections)
        {
            ct.ThrowIfCancellationRequested();
            HttpJsonConnection connection;
            try { connection = NormalizeConnection(item); }
            catch (ArgumentException) { throw new InvalidDataException("Invalid HTTP JSON music connections."); }
            if (!identities.Add(connection.SourceId) || !endpoints.Add((connection.ServerAddress, connection.Definition)))
                throw new InvalidDataException("Invalid HTTP JSON music connections.");
            normalized.Add(connection);
        }
        return normalized.ToArray();
    }

    public async Task SaveAsync(HttpJsonConnection connection, CancellationToken ct = default)
    {
        connection = NormalizeConnection(connection);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var writer = await LockAsync(timeout.Token);
        var entries = (await LoadAsync(ct)).ToList();
        var previous = entries.SingleOrDefault(item => item.SourceId == connection.SourceId);
        if (previous is not null && !SameEndpoint(previous, connection))
            throw new InvalidOperationException("A different catalog endpoint or field mapping requires a new music source.");
        if (entries.Any(item => item.SourceId != connection.SourceId && SameEndpoint(item, connection)))
            throw new InvalidOperationException("This catalog already has a connection.");
        if (previous is null && entries.Count >= MaximumConnections)
            throw new InvalidOperationException("Too many HTTP JSON music connections.");
        if (previous is not null) entries.Remove(previous);
        entries.Add(connection);
        await CommitAsync(entries, ct);
    }

    public async Task RemoveAsync(Guid sourceId, CancellationToken ct = default)
    {
        if (sourceId == Guid.Empty) throw new ArgumentException("Invalid source identity.", nameof(sourceId));
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var writer = await LockAsync(timeout.Token);
        var entries = (await LoadAsync(ct)).ToList();
        if (entries.RemoveAll(item => item.SourceId == sourceId) == 0) return;
        await CommitAsync(entries, ct);
    }

    public static HttpJsonConnection NormalizeConnection(HttpJsonConnection connection)
    {
        if (connection is null || connection.SourceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(connection.Name) || connection.Name.Length > 120 || connection.Name.Any(char.IsControl) ||
            connection.Definition is null)
            throw new ArgumentException("Invalid HTTP JSON music connection.", nameof(connection));
        return connection with
        {
            Name = connection.Name.Trim(),
            ServerAddress = HttpJsonClient.NormalizeServerAddress(connection.ServerAddress),
            Definition = HttpJsonDefinition.Normalize(connection.Definition)
        };
    }

    private static bool SameEndpoint(HttpJsonConnection left, HttpJsonConnection right) =>
        string.Equals(left.ServerAddress, right.ServerAddress, StringComparison.Ordinal) && left.Definition == right.Definition;

    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(50, ct); }
        }
    }

    private async Task CommitAsync(List<HttpJsonConnection> connections, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, connections.ToArray()), JsonOptions);
        if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("Invalid HTTP JSON music connections.");
        string pending = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
            }
#if DEBUG
            commitBoundary?.Invoke(false);
#endif
            ct.ThrowIfCancellationRequested();
            File.Move(pending, file, true);
#if DEBUG
            commitBoundary?.Invoke(true);
#endif
        }
        finally
        {
            try { File.Delete(pending); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate saved connection field.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
