using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// Independent consumer: deliberately has no reference to the player, Core or Infrastructure.
try
{
    string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FnMusic.Native");
    string command = "snapshot";
    Guid? instance = null;
    int count = 5, offset = 0, limit = 32;
    Guid? generation = null;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--data-directory": directory = Path.GetFullPath(args[++i]); break;
            case "--instance": instance = Guid.Parse(args[++i]); break;
            case "--count": count = int.Parse(args[++i]); break;
            case "--offset": offset = int.Parse(args[++i]); break;
            case "--limit": limit = int.Parse(args[++i]); break;
            case "--track-generation": generation = Guid.Parse(args[++i]); break;
            case "list" or "snapshot" or "lyrics" or "subscribe": command = args[i]; break;
            default: throw new ArgumentException();
        }
    }
    if (count is < 1 or > 100 || offset < 0 || limit is < 1 or > 64) throw new ArgumentException();
    var records = new List<Discovery>();
    string discoveryDirectory = Path.Combine(directory, "playback-information");
    if (Directory.Exists(discoveryDirectory))
    {
        foreach (string file in Directory.EnumerateFiles(discoveryDirectory, "*.json").Take(128))
        {
            try
            {
                // Discovery is untrusted, including stale files after abrupt process termination.
                if (new FileInfo(file).Length > 4096) continue;
                using var stream = File.OpenRead(file);
                var bounded = new byte[4097];
                int read = await stream.ReadAtLeastAsync(bounded, bounded.Length, throwOnEndOfStream: false);
                if (read > 4096) continue;
                using var document = JsonDocument.Parse(bounded.AsMemory(0, read), new JsonDocumentOptions { MaxDepth = 4 });
                var root = document.RootElement;
                int version = root.GetProperty("protocolVersion").GetInt32();
                Guid id = root.GetProperty("instanceId").GetGuid();
                string? pipe = root.GetProperty("pipeName").GetString();
                const string prefix = "fnmusic-info-v1-";
                if (version == 1 && pipe is not null && pipe.StartsWith(prefix, StringComparison.Ordinal)
                    && pipe.Length == prefix.Length + 32 && Guid.TryParseExact(pipe[prefix.Length..], "N", out _)
                    && (instance is null || instance == id)) records.Add(new(version, id, pipe));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or KeyNotFoundException or FormatException) { }
        }
    }
    if (command == "list")
    {
        Console.WriteLine(JsonSerializer.Serialize(records, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 0;
    }
    // Probe discoveries until a live server is found. Never delete records belonging to the application.
    foreach (var record in records)
    {
        using var pipe = new NamedPipeClientStream(".", record.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(connectTimeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { continue; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(command == "subscribe" ? count * 6 + 5 : 10));
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        { version = 1, command, id = "example", offset, limit, trackGeneration = generation });
        await pipe.WriteAsync(request, timeout.Token);
        await pipe.WriteAsync(new byte[] { (byte)'\n' }, timeout.Token);
        for (int i = 0; i < (command == "subscribe" ? count : 1); i++)
        {
            var response = await ReadFrameAsync(pipe, timeout.Token);
            if (response is null) throw new IOException();
            using var parsed = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 16 });
            Console.WriteLine(new UTF8Encoding(false, true).GetString(response));
            if (!parsed.RootElement.GetProperty("ok").GetBoolean()) return 3;
        }
        return 0;
    }
    Console.Error.WriteLine("No available playback information instance. Enable the interface in FNMusic settings.");
    return 2;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException
    or JsonException or ArgumentException or IndexOutOfRangeException or InvalidOperationException or KeyNotFoundException
    or FormatException or OverflowException)
{
    Console.Error.WriteLine("Information request failed or arguments are invalid. Commands: list, snapshot, lyrics, subscribe.");
    return 1;
}

static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
{
    using var bytes = new MemoryStream();
    var buffer = new byte[1];
    while (await stream.ReadAsync(buffer, cancellationToken) != 0)
    {
        if (buffer[0] == (byte)'\n') return bytes.ToArray();
        if (bytes.Length >= 65536) throw new InvalidDataException();
        bytes.WriteByte(buffer[0]);
    }
    return bytes.Length == 0 ? null : throw new InvalidDataException();
}

internal sealed record Discovery(int ProtocolVersion, Guid InstanceId, string PipeName);
