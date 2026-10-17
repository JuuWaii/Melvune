using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Melvune.Core;

namespace Melvune.Infrastructure;

/// <summary>Anonymous same-origin JSON catalogs. Media locations live only in this instance's memory.</summary>
public sealed class HttpJsonClient : IMusicPlaybackSource, IDisposable
{
    private const int MaximumTracks = 10000;
    private sealed record Assets(Uri Playback, Uri? Cover, Uri? Lyrics);
    private readonly Uri server;
    private readonly HttpJsonDefinition definition;
    private readonly Func<HttpMessageHandler> handlerFactory;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private IReadOnlyDictionary<string, Assets> assets = new Dictionary<string, Assets>();
    private int disposed;
    public MusicSource Source { get; }

    public HttpJsonClient(Guid sourceId, string name, string serverAddress, HttpJsonDefinition definition,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        if (sourceId == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(char.IsControl))
            throw new ArgumentException("Invalid music source");
        server = new Uri(NormalizeServerAddress(serverAddress));
        this.definition = HttpJsonDefinition.Normalize(definition);
        this.handlerFactory = handlerFactory ?? (() => new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null, DefaultProxyCredentials = null });
        http = CreateClient(); lifetimeToken = lifetime.Token;
        Source = new(sourceId, name.Trim(), "http-json", MusicSourceCapabilities.Browse | MusicSourceCapabilities.Playback |
            MusicSourceCapabilities.Search | MusicSourceCapabilities.Artwork | MusicSourceCapabilities.Lyrics);
    }

    public static string NormalizeServerAddress(string address)
    {
        string value = address?.Trim() ?? "";
        if (value.Length is < 1 or > 2048 || value.Any(char.IsControl) || value.Contains('\\') ||
            value.Contains('?') || value.Contains('#') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0)
            throw new ArgumentException("Invalid HTTP JSON server");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    public async Task CheckConnectionAsync(CancellationToken ct = default)
    {
        using var page = await ReadPageAsync(definition.FirstPage, ct).ConfigureAwait(false);
        var rows = ReadRows(page.RootElement);
        var total = ReadTotal(page.RootElement);
        if (total < rows.GetArrayLength() || (total > rows.GetArrayLength() && rows.GetArrayLength() < definition.PageSize)) throw Invalid();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray()) if (!seen.Add(ParseTrack(row).Track.Id)) throw Invalid();
    }

    public async Task<IReadOnlyList<MusicTrack>> LoadTracksAsync(CancellationToken ct = default)
    {
        List<MusicTrack> tracks = [];
        Dictionary<string, Assets> next = new(StringComparer.Ordinal);
        int? expectedTotal = null; bool first = true;
        for (int offset = 0; offset <= MaximumTracks; offset++)
        {
            using var page = await ReadPageAsync(checked(definition.FirstPage + offset), ct).ConfigureAwait(false);
            var rows = ReadRows(page.RootElement);
            int? total = ReadTotal(page.RootElement);
            if (first) { expectedTotal = total; first = false; }
            else if (total != expectedTotal) throw Invalid();
            foreach (var row in rows.EnumerateArray())
            {
                var parsed = ParseTrack(row);
                if (tracks.Count >= MaximumTracks || !next.TryAdd(parsed.Track.Id, parsed.Assets)) throw Invalid();
                tracks.Add(parsed.Track);
            }
            if (expectedTotal.HasValue && tracks.Count > expectedTotal.Value) throw Invalid();
            if (expectedTotal == tracks.Count || rows.GetArrayLength() < definition.PageSize)
            {
                if (expectedTotal.HasValue && tracks.Count != expectedTotal.Value) throw Invalid();
                ct.ThrowIfCancellationRequested(); lifetimeToken.ThrowIfCancellationRequested();
                Volatile.Write(ref assets, next);
                return tracks.ToArray();
            }
        }
        throw Invalid();
    }

    private JsonElement ReadRows(JsonElement root)
    {
        var rows = ReadMember(root, definition.ItemsPath, rootWhenEmpty: true);
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > definition.PageSize) throw Invalid();
        return rows;
    }
    private int? ReadTotal(JsonElement root)
    {
        var total = ReadMember(root, definition.TotalPath);
        if (total.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out int number) || number is < 0 or > MaximumTracks) throw Invalid();
        return number;
    }
    private (MusicTrack Track, Assets Assets) ParseTrack(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) throw Invalid();
        var rawId = ReadMember(row, definition.IdPath);
        string? id = rawId.ValueKind == JsonValueKind.String ? rawId.GetString() :
            rawId.ValueKind == JsonValueKind.Number && rawId.TryGetInt64(out long number) && number >= 0 ? number.ToString(CultureInfo.InvariantCulture) : null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw Invalid();
        double duration = 0;
        var rawDuration = ReadMember(row, definition.DurationPath);
        if (rawDuration.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (rawDuration.ValueKind != JsonValueKind.Number || !rawDuration.TryGetDouble(out duration) || !double.IsFinite(duration)) throw Invalid();
            if (definition.DurationUnit == "milliseconds") duration /= 1000;
            if (duration < 0 || duration > 864000) throw Invalid();
        }
        var playback = ResolveUrl(ReadText(row, definition.PlayUrlPath, 4096) ?? throw Invalid());
        var coverText = ReadText(row, definition.CoverUrlPath, 4096);
        var lyricText = ReadText(row, definition.LyricsUrlPath, 4096);
        Uri? cover = string.IsNullOrEmpty(coverText) ? null : ResolveUrl(coverText);
        Uri? lyrics = string.IsNullOrEmpty(lyricText) ? null : ResolveUrl(lyricText);
        return (new MusicTrack(id, ReadText(row, definition.TitlePath, 4096) ?? "未命名曲目",
            ReadText(row, definition.ArtistPath, 4096) ?? "", duration, false)
        { SourceInstanceId = Source.Id, SourceLabel = Source.Name, Album = ReadText(row, definition.AlbumPath, 4096) ?? "", CoverId = cover is null ? null : id },
            new(playback, cover, lyrics));
    }
    private static JsonElement ReadMember(JsonElement root, string path, bool rootWhenEmpty = false)
    {
        if (path.Length == 0) return rootWhenEmpty ? root : default;
        foreach (string member in path.Split('.'))
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(member, out var next)) return default;
            root = next;
        }
        return root;
    }
    private static string? ReadText(JsonElement root, string path, int maximum)
    {
        var value = ReadMember(root, path);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        string text = value.GetString()!;
        if (text.Length > maximum || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw Invalid();
        return text;
    }
    private Uri ResolveUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\') || value.Contains('?') || value.Contains('#') ||
            !Uri.TryCreate(server, value, out var uri) || uri.Scheme != server.Scheme || uri.Host != server.Host || uri.Port != server.Port ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw Invalid();
        return uri;
    }

    private async Task<JsonDocument> ReadPageAsync(int page, CancellationToken ct)
    {
        var url = new Uri(server, definition.ListPath + "?" + definition.PageParameter + "=" + page.ToString(CultureInfo.InvariantCulture) +
            "&" + definition.PageSizeParameter + "=" + definition.PageSize.ToString(CultureInfo.InvariantCulture));
        byte[] bytes = await ReadAssetAsync(url, 8 * 1024 * 1024, false, false, ct).ConfigureAwait(false) ?? throw Invalid();
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { throw Invalid(); }
    }

    public async Task<Stream> OpenAsync(TrackReference track, CancellationToken ct = default)
    {
        var asset = GetAssets(track);
        using var timeout = CreateTimeout(ct);
        try { return await HttpRangeStream.OpenAsync(CreateClient(), asset.Playback, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetimeToken.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (IOException) { throw new MusicApiException(MusicFailure.Unavailable); }
    }
    public Task<byte[]?> ReadCoverAsync(MusicTrack track, CancellationToken ct = default)
    {
        var asset = GetAssets(track.Reference);
        return asset.Cover is null ? Task.FromResult<byte[]?>(null) : ReadAssetAsync(asset.Cover, 4 * 1024 * 1024, true, true, ct);
    }
    public async Task<Lyrics> GetLyricsAsync(MusicTrack track, CancellationToken ct = default)
    {
        var asset = GetAssets(track.Reference);
        if (asset.Lyrics is null) return Lyrics.Empty;
        var bytes = await ReadAssetAsync(asset.Lyrics, 512000, true, false, ct).ConfigureAwait(false);
        if (bytes is null) return Lyrics.Empty;
        try { return Lyrics.Parse(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF')); }
        catch (ArgumentException) { throw Invalid(); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { throw Invalid(); }
    }
    private Assets GetAssets(TrackReference track)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (track.SourceInstanceId != Source.Id || string.IsNullOrEmpty(track.TrackId) ||
            !Volatile.Read(ref assets).TryGetValue(track.TrackId, out var asset)) throw new MusicApiException(MusicFailure.Rejected);
        return asset;
    }
    private async Task<byte[]?> ReadAssetAsync(Uri url, int limit, bool optional, bool image, CancellationToken ct)
    {
        using var timeout = CreateTimeout(ct);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (optional && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented) return null;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new MusicApiException(MusicFailure.Unauthorized);
            if ((int)response.StatusCode is >= 300 and < 400) throw new MusicApiException(MusicFailure.RedirectRejected);
            if (!response.IsSuccessStatusCode) throw new MusicApiException(MusicFailure.Unavailable);
            if (image && (response.Content.Headers.ContentType?.MediaType is not { } media || !media.StartsWith("image/", StringComparison.OrdinalIgnoreCase))) throw Invalid();
            if (response.Content.Headers.ContentLength > limit) throw Invalid();
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream(); byte[] buffer = new byte[16384];
            while (true)
            {
                int count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (count == 0) return output.ToArray();
                if (output.Length + count > limit) throw Invalid();
                output.Write(buffer, 0, count);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetimeToken.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (IOException) { throw new MusicApiException(MusicFailure.Unavailable); }
    }
    private HttpClient CreateClient() => new(handlerFactory()) { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15)); return timeout;
    }
    private static MusicApiException Invalid() => new(MusicFailure.InvalidResponse);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); http.Dispose(); lifetime.Dispose();
    }
}
