using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Melvune.Core;

namespace Melvune.Infrastructure;

/// <summary>The public, read-only v1 wire contract. No source or account data is accepted.</summary>
public static class PlaybackInformationProtocol
{
    public const int MaximumRequestBytes = 4096;
    public const int MaximumResponseBytes = 65536;
    public const int MaximumClients = 4;
    public const int MaximumPageLines = 64;
    public const int MaximumRequestsPerConnection = 256;
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 16 };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static byte[] Serialize(PlaybackInformationResponse response)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        if (bytes.Length > MaximumResponseBytes) throw new InvalidDataException("Response exceeds protocol boundary.");
        return bytes;
    }

    /// <summary>Reads bytes before decoding; invalid UTF-8 and oversized frames cannot allocate unbounded strings.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var frame = new MemoryStream(Math.Min(maximumBytes, 1024));
        var next = new byte[1];
        while (true)
        {
            int count = await stream.ReadAsync(next, cancellationToken).ConfigureAwait(false);
            if (count == 0) return frame.Length == 0 ? null : throw new InvalidDataException("Incomplete frame.");
            if (next[0] == (byte)'\n') return frame.ToArray();
            if (frame.Length >= maximumBytes) throw new InvalidDataException("Frame exceeds protocol boundary.");
            frame.WriteByte(next[0]);
        }
    }

    public static PlaybackInformationRequest ParseRequest(byte[] bytes)
    {
        // Validate UTF-8 rather than silently replacing malformed bytes.
        _ = new UTF8Encoding(false, true).GetString(bytes);
        var request = JsonSerializer.Deserialize<PlaybackInformationRequest>(bytes, JsonOptions)
            ?? throw new JsonException();
        if (request.Id is { Length: > 64 }) throw new JsonException();
        return request;
    }
}

public sealed record PlaybackInformationDiscovery(int ProtocolVersion, Guid InstanceId, string PipeName);
public sealed record PlaybackInformationRequest(int Version, string? Command, string? Id = null,
    int Offset = 0, int Limit = 32, Guid? TrackGeneration = null);
public sealed record PlaybackInformationResponse(int Version, string? Id, bool Ok, string Type,
    object? Data = null, string? Error = null);
public sealed record PlaybackInformationLyricsPage(Guid? TrackGeneration, PlaybackLyricsKind Kind,
    int Offset, int TotalLines, int? NextOffset, bool IsLimited, IReadOnlyList<LyricLine> Lines);
