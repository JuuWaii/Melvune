using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Melvune.Core;

namespace Melvune.Infrastructure;

public sealed record EmbyCredentials(string UserId, string AccessToken)
{
    public override string ToString() => "EmbyCredentials { Redacted }";
}

/// <summary>Read-only user-scoped Emby source. Credentials are headers, never URL parameters.</summary>
public sealed class EmbyClient : IMusicPlaybackSource, IDisposable
{
    private const int PageSize = 250;
    private const int MaximumTracks = 10000;
    private const string ClientIdentityHeader = "Emby Client=\"Melvune\", Device=\"Windows\", DeviceId=\"" + ProductIdentity.EmbyDeviceId + "\", Version=\"0.2\"";
    private readonly Uri server;
    private readonly EmbyCredentials credentials;
    private readonly Func<HttpMessageHandler> handlerFactory;
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private int disposed;
    public MusicSource Source { get; }
    public Guid SourceInstanceId => Source.Id;

    public EmbyClient(Guid sourceId, string name, string server, EmbyCredentials credentials,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        if (sourceId == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(char.IsControl))
            throw new ArgumentException("Invalid music source");
        ValidateCredentials(credentials);
        this.server = new Uri(NormalizeServerAddress(server));
        this.credentials = credentials;
        this.handlerFactory = handlerFactory ?? DefaultHandler;
        http = CreateClient();
        lifetimeToken = lifetime.Token;
        Source = new(sourceId, name.Trim(), "emby", MusicSourceCapabilities.Browse | MusicSourceCapabilities.Playback |
            MusicSourceCapabilities.Search | MusicSourceCapabilities.Artwork);
    }

    public static string NormalizeServerAddress(string address) => EmbyConnectionStore.NormalizeServerAddress(address);

    public static async Task<EmbyCredentials> AuthenticateAsync(string server, string username, string password,
        CancellationToken ct = default, Func<HttpMessageHandler>? handlerFactory = null)
    {
        var endpoint = new Uri(new Uri(NormalizeServerAddress(server)), "Users/AuthenticateByName");
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username.Any(char.IsControl) ||
            password is null || password.Length > 4096)
            throw new ArgumentException("Invalid music credentials");
        using var transport = new HttpClient(new ScopeHandler(new Uri(NormalizeServerAddress(server)), null,
            (handlerFactory ?? DefaultHandler)())) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            { Content = new StringContent(JsonSerializer.Serialize(new { Username = username.Trim(), Pw = password }), Encoding.UTF8, "application/json") };
            using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            ValidateStatus(response);
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 1024 * 1024, timeout.Token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("User", out var user) || user.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();
            var result = new EmbyCredentials(ReadIdentifier(user, "Id") ?? throw InvalidResponse(),
                ReadString(root, "AccessToken", 4096) ?? throw InvalidResponse());
            try { ValidateCredentials(result); } catch (ArgumentException) { throw InvalidResponse(); }
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (IOException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    public async Task CheckConnectionAsync(CancellationToken ct = default)
    {
        using var document = await RequestJsonAsync("Users/" + Escape(credentials.UserId), [], ct).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object || ReadIdentifier(document.RootElement, "Id") != credentials.UserId)
            throw InvalidResponse();
    }

    public async Task<IReadOnlyList<MusicTrack>> LoadTracksAsync(CancellationToken ct = default)
    {
        List<MusicTrack> result = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        int? expectedTotal = null;
        while (true)
        {
            using var document = await RequestJsonAsync("Users/" + Escape(credentials.UserId) + "/Items",
                [("Recursive", "true"), ("IncludeItemTypes", "Audio"), ("StartIndex", result.Count.ToString(CultureInfo.InvariantCulture)),
                ("Limit", PageSize.ToString(CultureInfo.InvariantCulture)), ("SortBy", "SortName,Id"), ("SortOrder", "Ascending"),
                ("Fields", "AlbumId,ArtistItems,RunTimeTicks,PrimaryImageAspectRatio"), ("EnableTotalRecordCount", "true")], ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("TotalRecordCount", out var totalValue) ||
                totalValue.ValueKind != JsonValueKind.Number || !totalValue.TryGetInt32(out int total) || total is < 0 or > MaximumTracks ||
                expectedTotal is not null && expectedTotal != total || !root.TryGetProperty("Items", out var items) ||
                items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > PageSize || result.Count + items.GetArrayLength() > total)
                throw InvalidResponse();
            if (root.TryGetProperty("StartIndex", out var start) && (start.ValueKind != JsonValueKind.Number || !start.TryGetInt32(out int offset) || offset != result.Count)) throw InvalidResponse();
            expectedTotal = total;
            if (items.GetArrayLength() == 0 && result.Count != total) throw InvalidResponse();
            foreach (var item in items.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var track = ParseTrack(item);
                if (!ids.Add(track.Id)) throw InvalidResponse();
                result.Add(track);
            }
            if (result.Count == total) return result.ToArray();
        }
    }

    private MusicTrack ParseTrack(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || ReadString(item, "Type", 64) != "Audio") throw InvalidResponse();
        string id = ReadIdentifier(item, "Id") ?? throw InvalidResponse();
        double duration = 0;
        if (item.TryGetProperty("RunTimeTicks", out var ticks) && ticks.ValueKind != JsonValueKind.Null)
        {
            if (ticks.ValueKind != JsonValueKind.Number || !ticks.TryGetInt64(out long value) || value < 0 || value > 864000L * TimeSpan.TicksPerSecond) throw InvalidResponse();
            duration = (double)value / TimeSpan.TicksPerSecond;
        }
        List<string> names = [];
        if (item.TryGetProperty("Artists", out var artists) && artists.ValueKind != JsonValueKind.Null)
        {
            if (artists.ValueKind != JsonValueKind.Array || artists.GetArrayLength() > 100) throw InvalidResponse();
            foreach (var artist in artists.EnumerateArray())
            {
                if (artist.ValueKind != JsonValueKind.String || artist.GetString()!.Length > 4096) throw InvalidResponse();
                names.Add(artist.GetString()!);
            }
        }
        List<TrackArtist> artistItems = [];
        if (item.TryGetProperty("ArtistItems", out var namedArtists) && namedArtists.ValueKind != JsonValueKind.Null)
        {
            if (namedArtists.ValueKind != JsonValueKind.Array || namedArtists.GetArrayLength() > 100) throw InvalidResponse();
            foreach (var artist in namedArtists.EnumerateArray())
            {
                if (artist.ValueKind != JsonValueKind.Object) throw InvalidResponse();
                artistItems.Add(new(ReadIdentifier(artist, "Id") ?? throw InvalidResponse(), ReadString(artist, "Name", 4096) ?? ""));
            }
            if (artistItems.Select(artist => artist.Id).Distinct(StringComparer.Ordinal).Count() != artistItems.Count) throw InvalidResponse();
        }
        string? albumId = ReadIdentifier(item, "AlbumId");
        string? cover = null;
        if (item.TryGetProperty("ImageTags", out var imageTags) && imageTags.ValueKind != JsonValueKind.Null)
        {
            if (imageTags.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            if (!string.IsNullOrEmpty(ReadString(imageTags, "Primary", 256))) cover = id;
        }
        if (cover is null && !string.IsNullOrEmpty(ReadString(item, "AlbumPrimaryImageTag", 256))) cover = albumId;
        bool favorite = false;
        if (item.TryGetProperty("UserData", out var data) && data.ValueKind != JsonValueKind.Null)
        {
            if (data.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            if (data.TryGetProperty("IsFavorite", out var value))
            { if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidResponse(); favorite = value.GetBoolean(); }
        }
        string artistName = string.Join(" / ", names.Count > 0 ? names : artistItems.Select(artist => artist.Name));
        if (artistName.Length > 4096) artistName = artistName[..(char.IsHighSurrogate(artistName[4095]) ? 4095 : 4096)];
        return new(id, ReadString(item, "Name", 4096) ?? "未命名曲目", artistName, duration, false)
        {
            SourceInstanceId = Source.Id, SourceLabel = Source.Name, Album = ReadString(item, "Album", 4096) ?? "",
            AlbumId = albumId, CoverId = cover, Artists = artistItems.ToArray(), IsFavorite = favorite
        };
    }

    public Task<Stream> OpenAsync(TrackReference track, CancellationToken ct = default) => OpenPlaybackAsync(track, ct);
    public async Task<Stream> OpenPlaybackAsync(TrackReference track, CancellationToken ct = default)
    {
        ValidateReference(track);
        using var timeout = CreateTimeout(ct);
        try { return await HttpRangeStream.OpenAsync(CreateClient(), Endpoint("Audio/" + Escape(track.TrackId) + "/stream", [("static", "true")]), timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetimeToken.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (IOException) { throw new MusicApiException(MusicFailure.Unavailable); }
    }

    public async Task<byte[]> ReadCoverAsync(MusicTrack track, CancellationToken ct = default)
    {
        ValidateReference(track.Reference);
        if (string.IsNullOrEmpty(track.CoverId)) return [];
        if (!ValidId(track.CoverId)) throw new MusicApiException(MusicFailure.Rejected);
        using var timeout = CreateTimeout(ct);
        try
        {
            using var response = await http.GetAsync(Endpoint("Items/" + Escape(track.CoverId) + "/Images/Primary",
                [("MaxWidth", "1200"), ("MaxHeight", "1200")]), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            ValidateStatus(response);
            if (response.Content.Headers.ContentType?.MediaType is not { } type || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw InvalidResponse();
            return await ReadBoundedAsync(response.Content, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetimeToken.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (IOException) { throw new MusicApiException(MusicFailure.Unavailable); }
    }

    // Emby has no stable server-wide lyric contract for this client. Embedded lyrics remain handled by the decoder.
    public Task<Lyrics> ReadLyricsAsync(MusicTrack track, CancellationToken ct = default)
    { ValidateReference(track.Reference); ct.ThrowIfCancellationRequested(); return Task.FromResult(Lyrics.Empty); }

    private async Task<JsonDocument> RequestJsonAsync(string path, (string, string)[] fields, CancellationToken ct)
    {
        using var timeout = CreateTimeout(ct);
        try
        {
            using var response = await http.GetAsync(Endpoint(path, fields), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested(); ValidateStatus(response);
            return JsonDocument.Parse(await ReadBoundedAsync(response.Content, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetimeToken.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (IOException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (JsonException) { throw InvalidResponse(); }
    }
    private Uri Endpoint(string path, (string, string)[] fields) => new(server, path + (fields.Length == 0 ? "" : "?" + string.Join("&", fields.Select(field => Escape(field.Item1) + "=" + Escape(field.Item2)))));
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static void ValidateCredentials(EmbyCredentials value)
    {
        if (value is null || !ValidId(value.UserId) || value.AccessToken is not { Length: > 0 and <= 4096 } ||
            value.AccessToken.Any(character => character is < '!' or > '~' or '"' or '\\')) throw new ArgumentException("Invalid music credentials");
    }
    private void ValidateReference(TrackReference track)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (track.SourceInstanceId != Source.Id || !ValidId(track.TrackId)) throw new MusicApiException(MusicFailure.Rejected);
    }
    private static string? ReadString(JsonElement parent, string name, int limit)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > limit) throw InvalidResponse();
        return value.GetString();
    }
    private static string? ReadIdentifier(JsonElement parent, string name)
    { var value = ReadString(parent, name, 256); if (string.IsNullOrEmpty(value)) return null; if (!ValidId(value)) throw InvalidResponse(); return value; }
    private static MusicApiException InvalidResponse() => new(MusicFailure.InvalidResponse);
    private static void ValidateStatus(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new MusicApiException(MusicFailure.Unauthorized);
        if ((int)response.StatusCode is >= 300 and < 400) throw new MusicApiException(MusicFailure.RedirectRejected);
        if (!response.IsSuccessStatusCode) throw InvalidResponse();
    }
    private static HttpMessageHandler DefaultHandler() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
    private HttpClient CreateClient() => new(new ScopeHandler(server, credentials, handlerFactory())) { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15)); return timeout;
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw InvalidResponse();
        using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream(); byte[] buffer = new byte[16384];
        while (true)
        {
            ct.ThrowIfCancellationRequested(); int count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) throw InvalidResponse(); output.Write(buffer, 0, count);
        }
    }
    private sealed class ScopeHandler(Uri server, EmbyCredentials? credentials, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri;
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != server.Scheme || uri.IdnHost != server.IdnHost || uri.Port != server.Port ||
                !uri.AbsolutePath.StartsWith(server.AbsolutePath, StringComparison.Ordinal)) throw new MusicApiException(MusicFailure.RedirectRejected);
            request.Headers.TryAddWithoutValidation("Authorization", ClientIdentityHeader);
            if (credentials is not null) request.Headers.Add("X-Emby-Token", credentials.AccessToken);
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized || (int)response.StatusCode is >= 300 and < 400)
            { try { ValidateStatus(response); } finally { response.Dispose(); } }
            return response;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); http.Dispose(); lifetime.Dispose();
    }
}
