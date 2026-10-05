using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FnMusic.Core;

namespace FnMusic.Infrastructure;

public sealed partial class NasApiClient : IDisposable
{
    private readonly HttpClient http;
    private readonly ServerEndpoint endpoint;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private int disposed;
    private string? token;
    private readonly object sessionGate = new();
    private long sessionVersion;
    public Guid SourceInstanceId { get; }
    public NasApiClient(ServerEndpoint endpoint, HttpMessageHandler? handler = null, Guid sourceInstanceId = default)
    {
        this.endpoint = endpoint;
        lifetimeToken = lifetime.Token;
        SourceInstanceId = sourceInstanceId;
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void RestoreSession(string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken) || sessionToken.Length > 16384 ||
            sessionToken.Any(c => c < 33 || c > 126 || c is ';' or ','))
            throw new MusicApiException(MusicFailure.InvalidResponse);
        lock (sessionGate) { token = sessionToken; sessionVersion++; }
    }
    public void ClearSession() { lock (sessionGate) { token = null; sessionVersion++; } }
    private (string? Token, long Version) CaptureSession()
    { lock (sessionGate) return (token, sessionVersion); }
    private void RejectSession(long requestVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sessionGate)
        {
            // 旧会话的失败不能清除新会话，也不能通知上层将新会话判为失效。
            if (sessionVersion != requestVersion) throw new MusicApiException(MusicFailure.Unavailable);
            token = null; sessionVersion++;
        }
        throw new MusicApiException(MusicFailure.Unauthorized);
    }

    public async Task<TrackPage> ListTracksAsync(int page, int size, CancellationToken ct)
    {
        if (page < 1 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        using var data = await RequestAsync(HttpMethod.Get, $"track/list?page={page}&size={size}", null, ct);
        return ParseTrackPage(data.RootElement);
    }

    public async Task<TrackPage> SearchTracksAsync(string query, int page, int size, CancellationToken ct)
    {
        if (page < 1 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        query = query.Trim();
        if (query.Length is 0 or > 256) throw new ArgumentException("Invalid search query", nameof(query));
        using var data = await RequestAsync(HttpMethod.Get,
            $"search/track?q={Uri.EscapeDataString(query)}&page={page}&size={size}", null, ct);
        var result = ParseTrackPage(data.RootElement);
        // 当前 NAS 搜索会忽略 page/size 并返回完整集合；只在条数等于 total 时本地分页。
        if (result.Tracks.Count == result.Total)
        {
            long offset = (long)(page - 1) * size;
            return new TrackPage(offset >= result.Total ? [] : result.Tracks.Skip((int)offset).Take(size).ToArray(), result.Total);
        }
        // 不把未知的超长部分结果截断后伪装成一页，否则可能静默遗漏曲目。
        if (result.Tracks.Count > size || result.Tracks.Count > result.Total)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        return result;
    }

    private TrackPage ParseTrackPage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out int count) || count < 0)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        List<MusicTrack> tracks = [];
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("guid", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
                throw new MusicApiException(MusicFailure.InvalidResponse);
            string title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "未命名曲目";
            string artist = item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                ? string.Join(" / ", artists.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String).Select(a => a.GetProperty("name").GetString())) : "";
            double duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out double ms) && double.IsFinite(ms) ? Math.Max(0, ms / 1000) : 0;
            bool cue = item.TryGetProperty("isCue", out var c) && c.ValueKind == JsonValueKind.True;
            var artistReferences = item.TryGetProperty("artists", out var refs) && refs.ValueKind == JsonValueKind.Array
                ? refs.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object
                    && ReadOptionalString(a, "guid") is { Length: > 0 and <= 256 }
                    && ReadOptionalString(a, "name") is { Length: > 0 })
                    .Select(a => new TrackArtist(a.GetProperty("guid").GetString()!, a.GetProperty("name").GetString()!))
                    .DistinctBy(a => a.Id).ToArray() : [];
            string? albumId = item.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object
                ? ReadOptionalString(album, "guid") : null;
            tracks.Add(new MusicTrack(id.GetString()!, title, artist, duration, cue)
            { SourceInstanceId = SourceInstanceId, CoverId = ReadOptionalString(item, "coverId"), Album = ReadAlbum(item),
                AlbumId = albumId is { Length: > 0 and <= 256 } ? albumId : null, Artists = artistReferences,
                IsFavorite = item.TryGetProperty("isFavorite", out var favorite) && favorite.ValueKind == JsonValueKind.True });
        }
        return new TrackPage(tracks, count);
    }

    public async Task<HttpRangeStream> OpenTrackStreamAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 256) throw new ArgumentException("Invalid track identifier");
        var session = CaptureSession();
        if (session.Token is null) throw new MusicApiException(MusicFailure.Unauthorized);
        var transport = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
        transport.DefaultRequestHeaders.Add("Cookie", "music-token=" + Uri.EscapeDataString(session.Token));
        return await HttpRangeStream.OpenAsync(transport, new Uri(endpoint.ApiUri, "track/stream?guid=" + Uri.EscapeDataString(id)), ct).ConfigureAwait(false);
    }

    public Task<HttpRangeStream> OpenTrackStreamAsync(TrackReference reference, CancellationToken ct)
    {
        ValidateReference(reference);
        return OpenTrackStreamAsync(reference.TrackId, ct);
    }
    private void ValidateReference(TrackReference reference)
    {
        if (reference.SourceInstanceId != SourceInstanceId) throw new MusicApiException(MusicFailure.Rejected);
    }

    public async Task<bool> CheckConnectionAsync(CancellationToken ct)
    {
        using var data = await RequestAsync(HttpMethod.Get, "initialization/state", null, ct);
        if (!data.RootElement.TryGetProperty("initialized", out var state) || state.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new MusicApiException(MusicFailure.InvalidResponse);
        return state.GetBoolean();
    }
    public async Task<MusicUser> GetCurrentUserAsync(CancellationToken ct)
    {
        using var data = await RequestAsync(HttpMethod.Get, "user/me", null, ct);
        return ParseUser(data.RootElement);
    }
    public async Task<string> LoginAsync(string username, string password, string deviceId, CancellationToken ct)
    {
        ClearSession();
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        string digest;
        try { digest = Convert.ToHexStringLower(SHA256.HashData(passwordBytes)); }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
        using var data = await RequestAsync(HttpMethod.Post, "user/password-login",
            new { username = username.Trim(), password = digest, deviceId }, ct);
        if (!data.RootElement.TryGetProperty("userToken", out var value) || value.ValueKind != JsonValueKind.String)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        if (!data.RootElement.TryGetProperty("user", out var user)) throw new MusicApiException(MusicFailure.InvalidResponse);
        _ = ParseUser(user);
        RestoreSession(value.GetString()!);
        return token!;
    }
    public async Task LogoutAsync(CancellationToken ct)
    {
        try { using var data = await RequestAsync(HttpMethod.Post, "user/logout", null, ct); }
        finally { ClearSession(); }
    }
    private static MusicUser ParseUser(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        return new MusicUser(name.GetString()!);
    }
    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
        // ResponseHeadersRead 不把正文读取计入 HttpClient.Timeout；整个 JSON 请求共用同一期限。
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var requestToken = deadline.Token;
        var session = CaptureSession();
        using var request = new HttpRequestMessage(method, new Uri(endpoint.ApiUri, path));
        if (session.Token is not null) request.Headers.Add("Cookie", "music-token=" + Uri.EscapeDataString(session.Token));
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            requestToken.ThrowIfCancellationRequested();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            requestToken.ThrowIfCancellationRequested();
            if ((int)response.StatusCode is >= 300 and < 400) throw new MusicApiException(MusicFailure.RedirectRejected);
            if (response.StatusCode == HttpStatusCode.Unauthorized) RejectSession(session.Version, requestToken);
            if (!response.IsSuccessStatusCode) throw new MusicApiException(MusicFailure.Rejected);
            // 即使 Content-Length 缺失也限制读取量，防止异常响应耗尽内存。
            using var input = await response.Content.ReadAsStreamAsync(requestToken);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, requestToken)) != 0)
            {
                if (output.Length + count > 2 * 1024 * 1024) throw new MusicApiException(MusicFailure.InvalidResponse);
                await output.WriteAsync(buffer.AsMemory(0, count), requestToken);
            }
            requestToken.ThrowIfCancellationRequested();
            using var envelope = JsonDocument.Parse(output.ToArray());
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int codeValue))
                throw new MusicApiException(MusicFailure.InvalidResponse);
            if (codeValue == 120001) RejectSession(session.Version, requestToken);
            if (codeValue is not (0 or 200)) throw new MusicApiException(MusicFailure.Rejected);
            var result = JsonDocument.Parse(root.TryGetProperty("data", out var data) ? data.GetRawText() : "null");
            try { requestToken.ThrowIfCancellationRequested(); return result; }
            catch { result.Dispose(); throw; }
        }
        catch (JsonException) { throw new MusicApiException(MusicFailure.InvalidResponse); }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // 收到响应头后 HttpClient.Dispose 不会取消正文读取，必须主动取消客户端生命周期。
        lifetime.Cancel();
        ClearSession(); http.Dispose(); lifetime.Dispose();
    }
    private static string? ReadOptionalString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string ReadAlbum(JsonElement item)
    {
        if (!item.TryGetProperty("album", out var album)) return "";
        return album.ValueKind == JsonValueKind.String ? album.GetString()! : album.ValueKind == JsonValueKind.Object ? ReadOptionalString(album, "name") ?? "" : "";
    }
}
