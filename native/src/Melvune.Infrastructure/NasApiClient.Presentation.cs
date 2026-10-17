using System.Net;
using System.Text.Json;
using Melvune.Core;

namespace Melvune.Infrastructure;

public sealed partial class NasApiClient
{
    public async Task<Lyrics> GetLyricsAsync(TrackReference track, CancellationToken ct)
    {
        ValidateReference(track);
        using var data = await RequestAsync(HttpMethod.Get, "lyric/list?trackGUID=" + EncodeCollectionId(track.TrackId), null, ct);
        var root = data.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        string? preferred = ReadOptionalString(root, "preferred");
        var candidates = list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(ReadOptionalString(item, "content")))
            .OrderByDescending(item => preferred is not null && ReadOptionalString(item, "guid") == preferred)
            .ThenByDescending(item => item.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Number && source.TryGetInt32(out int value) ? value switch { 1 => 4, 2 => 3, 4 => 2, _ => 1 } : 0);
        Lyrics? plain = null;
        foreach (var item in candidates)
        {
            double offset = item.TryGetProperty("offset", out var delay) && delay.ValueKind == JsonValueKind.Number && delay.TryGetDouble(out var number) ? number : 0;
            Lyrics parsed;
            try { parsed = Lyrics.Parse(ReadOptionalString(item, "content")!, offset); }
            catch (ArgumentException) { continue; }
            if (parsed.Lines.Count == 0) continue;
            if (parsed.IsTimed || (preferred is not null && ReadOptionalString(item, "guid") == preferred)) return parsed;
            plain ??= parsed;
        }
        return plain ?? Lyrics.Empty;
    }

    public Task<byte[]?> GetCoverAsync(string coverId, CancellationToken ct) => GetCoverAsync(coverId, NasCoverSize.Medium, ct);

    public async Task<byte[]?> GetCoverAsync(string coverId, NasCoverSize size, CancellationToken ct)
    {
        _ = EncodeCollectionId(coverId);
        if (!Enum.IsDefined(size)) throw new ArgumentOutOfRangeException(nameof(size));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
        // ResponseHeadersRead 之后仍使用同一请求期限，关闭客户端也取消正文读取。
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var requestToken = deadline.Token;
        var session = CaptureSession();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.ApiUri, "static/cover?coverId=" + Uri.EscapeDataString(coverId) + "&size=" + ((int)size).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (session.Token is not null) request.Headers.Add("Cookie", "music-token=" + Uri.EscapeDataString(session.Token));
        try
        {
            requestToken.ThrowIfCancellationRequested();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            requestToken.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if ((int)response.StatusCode is >= 300 and < 400) throw new MusicApiException(MusicFailure.RedirectRejected);
            if (response.StatusCode == HttpStatusCode.Unauthorized) RejectSession(session.Version, requestToken);
            if (!response.IsSuccessStatusCode) throw new MusicApiException(MusicFailure.Rejected);
            if (response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif" or "image/bmp"))
                throw new MusicApiException(MusicFailure.InvalidResponse);
            using var input = await response.Content.ReadAsStreamAsync(requestToken);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, requestToken)) > 0)
            {
                if (output.Length + count > 4 * 1024 * 1024) throw new MusicApiException(MusicFailure.InvalidResponse);
                output.Write(buffer, 0, count);
            }
            requestToken.ThrowIfCancellationRequested();
            return output.ToArray();
        }
        catch (HttpRequestException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (IOException) { throw new MusicApiException(MusicFailure.Unavailable); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MusicApiException(MusicFailure.Unavailable); }
    }
}
