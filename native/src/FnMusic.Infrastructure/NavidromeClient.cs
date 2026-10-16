using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FnMusic.Core;

namespace FnMusic.Infrastructure;

public sealed record NavidromeCredentials(string Username, string Token, string Salt)
{
    public static NavidromeCredentials Create(string username, string password) => NavidromeClient.CreateCredentials(username, password);
    public override string ToString() => "NavidromeCredentials { Redacted }";
}

/// <summary>Read-only Subsonic transport. Requests never follow redirects or expose authenticated URLs in errors.</summary>
public sealed class NavidromeClient : IMusicPlaybackSource, IDisposable
{
    private const int PageSize = 250;
    private const int MaximumTracks = 100000;
    private const int MaximumAlbums = 10000;
    private const int MaximumAlbumTracks = 10000;
    private readonly Uri server;
    private readonly NavidromeCredentials credentials;
    private readonly Func<HttpMessageHandler> handlerFactory;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private int disposed;
    public MusicSource Source { get; }

    public NavidromeClient(Guid sourceId, string name, string serverAddress, NavidromeCredentials credentials,
        Func<HttpMessageHandler>? handlerFactory = null, string provider = "navidrome")
    {
        if (sourceId == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(char.IsControl))
            throw new ArgumentException("Invalid music source");
        ValidateCredentials(credentials);
        provider = NavidromeConnectionStore.NormalizeProvider(provider);
        server = new Uri(NormalizeServerAddress(serverAddress));
        this.credentials = credentials;
        this.handlerFactory = handlerFactory ?? (() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        http = CreateClient();
        lifetimeToken = lifetime.Token;
        Source = new(sourceId, name.Trim(), provider, MusicSourceCapabilities.Browse | MusicSourceCapabilities.Playback |
            MusicSourceCapabilities.Search | MusicSourceCapabilities.Artwork | MusicSourceCapabilities.Lyrics);
    }

    public static string NormalizeServerAddress(string address) => NavidromeConnectionStore.NormalizeServerAddress(address);

    public static NavidromeCredentials CreateCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username.Any(char.IsControl) ||
            string.IsNullOrEmpty(password) || password.Length > 4096)
            throw new ArgumentException("Invalid music credentials");
        string salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        byte[] bytes = Encoding.UTF8.GetBytes(password + salt);
        try { return new(username.Trim(), Convert.ToHexStringLower(MD5.HashData(bytes)), salt); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void ValidateCredentials(NavidromeCredentials value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Username) || value.Username.Length > 256 || value.Username.Any(char.IsControl) ||
            value.Token is not { Length: 32 } || !value.Token.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(value.Salt) ||
            value.Salt.Length > 128 || value.Salt.Any(char.IsControl))
            throw new ArgumentException("Invalid music credentials");
    }

    public async Task CheckConnectionAsync(CancellationToken ct = default)
    {
        using var document = await RequestJsonAsync("ping", [], false, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MusicTrack>> LoadTracksAsync(CancellationToken ct = default)
    {
        if (Source.Provider == "subsonic") return await LoadAlbumTracksAsync(ct).ConfigureAwait(false);
        List<MusicTrack> tracks = [];
        HashSet<string> identities = new(StringComparer.Ordinal);
        for (int offset = 0; ; offset += PageSize)
        {
            using var document = await RequestJsonAsync("search3", [("query", ""), ("artistCount", "0"), ("albumCount", "0"),
                ("songCount", PageSize.ToString(CultureInfo.InvariantCulture)), ("songOffset", offset.ToString(CultureInfo.InvariantCulture))], false, ct).ConfigureAwait(false);
            var root = document!.RootElement.GetProperty("subsonic-response");
            if (!root.TryGetProperty("searchResult3", out var result)) return tracks.ToArray();
            if (result.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            if (!result.TryGetProperty("song", out var songs)) return tracks.ToArray();
            if (songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() > PageSize) throw InvalidResponse();
            foreach (var song in songs.EnumerateArray())
            {
                var track = ParseTrack(song);
                if (!identities.Add(track.Id) || tracks.Count >= MaximumTracks) throw InvalidResponse();
                tracks.Add(track);
            }
            if (songs.GetArrayLength() < PageSize) return tracks.ToArray();
        }
    }

    private async Task<IReadOnlyList<MusicTrack>> LoadAlbumTracksAsync(CancellationToken ct)
    {
        // Standard Subsonic does not promise that an empty search3 query lists every song.
        // Enumerate the ID3 album catalog with its documented pagination instead.
        List<MusicTrack> tracks = [];
        HashSet<string> albums = new(StringComparer.Ordinal), identities = new(StringComparer.Ordinal);
        for (int offset = 0; ; offset += PageSize)
        {
            using var document = await RequestJsonAsync("getAlbumList2", [("type", "alphabeticalByName"),
                ("size", PageSize.ToString(CultureInfo.InvariantCulture)),
                ("offset", offset.ToString(CultureInfo.InvariantCulture))], false, ct).ConfigureAwait(false);
            var body = document!.RootElement.GetProperty("subsonic-response");
            if (!body.TryGetProperty("albumList2", out var list)) throw InvalidResponse();
            if (list.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            if (!list.TryGetProperty("album", out var page)) return tracks.ToArray();
            if (page.ValueKind != JsonValueKind.Array || page.GetArrayLength() > PageSize) throw InvalidResponse();
            foreach (var album in page.EnumerateArray())
            {
                if (album.ValueKind != JsonValueKind.Object) throw InvalidResponse();
                string albumId = ReadIdentifier(album, "id") ?? throw InvalidResponse();
                int? listedCount = ReadSongCount(album);
                if (!albums.Add(albumId) || albums.Count > MaximumAlbums) throw InvalidResponse();
                using var detail = await RequestJsonAsync("getAlbum", [("id", albumId)], false, ct).ConfigureAwait(false);
                var detailBody = detail!.RootElement.GetProperty("subsonic-response");
                if (!detailBody.TryGetProperty("album", out var contents) || contents.ValueKind != JsonValueKind.Object ||
                    ReadIdentifier(contents, "id") != albumId) throw InvalidResponse();
                int? detailCount = ReadSongCount(contents);
                if (listedCount.HasValue && detailCount.HasValue && listedCount != detailCount) throw InvalidResponse();
                if (!contents.TryGetProperty("song", out var songs))
                {
                    if (listedCount > 0 || detailCount > 0) throw InvalidResponse();
                    continue;
                }
                if (songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() > MaximumAlbumTracks ||
                    (listedCount.HasValue && listedCount.Value != songs.GetArrayLength()) ||
                    (detailCount.HasValue && detailCount.Value != songs.GetArrayLength())) throw InvalidResponse();
                foreach (var song in songs.EnumerateArray())
                {
                    var track = ParseTrack(song);
                    if (!identities.Add(track.Id) || tracks.Count >= MaximumAlbumTracks) throw InvalidResponse();
                    tracks.Add(track);
                }
            }
            if (page.GetArrayLength() < PageSize) return tracks.ToArray();
        }
    }

    private static int? ReadSongCount(JsonElement album)
    {
        if (!album.TryGetProperty("songCount", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int count) ||
            count < 0 || count > MaximumAlbumTracks) throw InvalidResponse();
        return count;
    }

    private MusicTrack ParseTrack(JsonElement song)
    {
        if (song.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        string id = ReadIdentifier(song, "id") ?? throw InvalidResponse();
        double duration = 0;
        if (song.TryGetProperty("duration", out var value) && (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out duration) || !double.IsFinite(duration) || duration < 0 || duration > 864000)) throw InvalidResponse();
        string artist = ReadString(song, "artist", 4096) ?? "";
        string? artistId = ReadIdentifier(song, "artistId");
        string? folder = null;
        if (song.TryGetProperty("musicFolderId", out var musicFolder))
        {
            if (musicFolder.ValueKind == JsonValueKind.Number && musicFolder.TryGetInt64(out long number) && number >= 0)
                folder = number.ToString(CultureInfo.InvariantCulture);
            else folder = ReadIdentifier(song, "musicFolderId");
        }
        return new MusicTrack(id, ReadString(song, "title", 4096) ?? "未命名曲目", artist, duration, false)
        {
            SourceInstanceId = Source.Id, SourceLabel = Source.Name,
            Album = ReadString(song, "album", 4096) ?? "", AlbumId = ReadIdentifier(song, "albumId"),
            CoverId = ReadIdentifier(song, "coverArt"), LibraryIds = string.IsNullOrEmpty(folder) ? [] : [folder],
            Artists = string.IsNullOrEmpty(artistId) ? [] : [new TrackArtist(artistId, artist)]
        };
    }

    public async Task<Stream> OpenAsync(TrackReference track, CancellationToken ct = default)
    {
        ValidateReference(track);
        using var timeout = CreateTimeout(ct);
        // The range stream owns this dedicated client. Disposing the catalog connection must not close an opened stream.
        var transport = CreateClient();
        try { return await HttpRangeStream.OpenAsync(transport, Endpoint("stream", [("id", track.TrackId), ("format", "raw")]), timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetimeToken.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (IOException) { throw new MusicApiException(MusicFailure.Unavailable); }
    }

    public async Task<Stream?> OpenArtworkAsync(MusicTrack track, int maxEdge, CancellationToken ct = default)
    {
        ValidateReference(track.Reference);
        if (maxEdge is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxEdge));
        if (string.IsNullOrEmpty(track.CoverId)) return null;
        if (track.CoverId.Length > 256 || track.CoverId.Any(char.IsControl)) throw new MusicApiException(MusicFailure.Rejected);
        using var timeout = CreateTimeout(ct);
        try
        {
            using var response = await http.GetAsync(Endpoint("getCoverArt", [("id", track.CoverId), ("size", maxEdge.ToString(CultureInfo.InvariantCulture))]),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented) return null;
            ValidateStatus(response);
            if (response.Content.Headers.ContentType?.MediaType is not { } type || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw InvalidResponse();
            return new MemoryStream(await ReadBoundedAsync(response.Content, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetimeToken.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (IOException) { throw new MusicApiException(MusicFailure.Unavailable); }
    }

    public async Task<Lyrics> GetLyricsAsync(MusicTrack track, CancellationToken ct = default)
    {
        ValidateReference(track.Reference);
        using var document = await RequestJsonAsync("getLyricsBySongId", [("id", track.Id)], true, ct).ConfigureAwait(false);
        if (document is null) return Lyrics.Empty;
        var root = document.RootElement.GetProperty("subsonic-response");
        if (!root.TryGetProperty("lyricsList", out var list)) return Lyrics.Empty;
        if (list.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        if (!list.TryGetProperty("structuredLyrics", out var candidates)) return Lyrics.Empty;
        if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() > 100) throw InvalidResponse();
        Lyrics plain = Lyrics.Empty;
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object || !candidate.TryGetProperty("line", out var lines) || lines.ValueKind != JsonValueKind.Array ||
                lines.GetArrayLength() > 10000) throw InvalidResponse();
            if (!candidate.TryGetProperty("synced", out var sync) || sync.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidResponse();
            bool synced = sync.ValueKind == JsonValueKind.True;
            double offset = 0;
            if (candidate.TryGetProperty("offset", out var offsetValue) && (offsetValue.ValueKind != JsonValueKind.Number ||
                !offsetValue.TryGetDouble(out offset) || !double.IsFinite(offset) || Math.Abs(offset) > 864000000)) throw InvalidResponse();
            List<LyricLine> parsed = [];
            int characters = 0;
            foreach (var line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object) throw InvalidResponse();
                string text = ReadString(line, "value", 512000) ?? throw InvalidResponse();
                characters = checked(characters + text.Length);
                if (characters > 512000) throw InvalidResponse();
                double milliseconds = 0;
                if (synced && (!line.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Number ||
                    !start.TryGetDouble(out milliseconds) || !double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > 864000000)) throw InvalidResponse();
                // OpenSubsonic positive offsets show lyrics earlier; unsynced lines retain their appearance order.
                if (!string.IsNullOrWhiteSpace(text)) parsed.Add(new(synced ? Math.Max(0, (milliseconds - offset) / 1000) : 0, text));
            }
            if (parsed.Count == 0) continue;
            if (synced) return new Lyrics(parsed.OrderBy(line => line.Seconds).GroupBy(line => line.Seconds)
                .Select(group => new LyricLine(group.Key, string.Join("\n", group.Select(line => line.Text)))).ToArray(), true);
            if (plain.Lines.Count == 0) plain = new(parsed.ToArray(), false);
        }
        return plain;
    }

    private async Task<JsonDocument?> RequestJsonAsync(string method, (string, string)[] parameters, bool optionalLyrics, CancellationToken ct)
    {
        using var timeout = CreateTimeout(ct);
        try
        {
            using var response = await http.GetAsync(Endpoint(method, parameters), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (optionalLyrics && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented) return null;
            ValidateStatus(response);
            byte[] bytes = await ReadBoundedAsync(response.Content, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            try
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("subsonic-response", out var body) || body.ValueKind != JsonValueKind.Object) throw InvalidResponse();
                string? status = ReadString(body, "status", 32);
                if (status == "ok") return document;
                if (status != "failed" || !body.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
                    !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int number)) throw InvalidResponse();
                if (optionalLyrics && number == 70) { document.Dispose(); return null; }
                throw new MusicApiException(number is 40 or 50 ? MusicFailure.Unauthorized : MusicFailure.Rejected);
            }
            catch { document.Dispose(); throw; }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetimeToken.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (IOException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private Uri Endpoint(string method, (string, string)[] parameters)
    {
        var fields = new List<(string, string)> { ("u", credentials.Username), ("t", credentials.Token), ("s", credentials.Salt),
            ("v", "1.16.1"), ("c", "Melvune"), ("f", "json") };
        fields.AddRange(parameters);
        return new Uri(server, "rest/" + method + ".view?" + string.Join("&", fields.Select(field => Uri.EscapeDataString(field.Item1) + "=" + Uri.EscapeDataString(field.Item2))));
    }

    private HttpClient CreateClient() => new(handlerFactory()) { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return timeout;
    }
    private void ValidateReference(TrackReference reference)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (reference.SourceInstanceId != Source.Id || string.IsNullOrEmpty(reference.TrackId) || reference.TrackId.Length > 256 || reference.TrackId.Any(char.IsControl))
            throw new MusicApiException(MusicFailure.Rejected);
    }
    private static string? ReadString(JsonElement value, string property, int limit)
    {
        if (!value.TryGetProperty(property, out var text) || text.ValueKind == JsonValueKind.Null) return null;
        if (text.ValueKind != JsonValueKind.String) throw InvalidResponse();
        string result = text.GetString()!;
        if (result.Length > limit) throw InvalidResponse();
        return result;
    }
    private static string? ReadIdentifier(JsonElement value, string property)
    {
        string? result = ReadString(value, property, 256);
        if (result is null) return null;
        if (string.IsNullOrEmpty(result) || result.Any(char.IsControl)) throw InvalidResponse();
        return result;
    }
    private static MusicApiException InvalidResponse() => new(MusicFailure.InvalidResponse);
    private static void ValidateStatus(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new MusicApiException(MusicFailure.Unauthorized);
        if ((int)response.StatusCode is >= 300 and < 400) throw new MusicApiException(MusicFailure.RedirectRejected);
        if (!response.IsSuccessStatusCode) throw new MusicApiException(MusicFailure.Unavailable);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw InvalidResponse();
        using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) throw InvalidResponse();
            output.Write(buffer, 0, count);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); http.Dispose(); lifetime.Dispose();
    }
}
