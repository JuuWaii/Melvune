using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;

internal static class EmbyVerification
{
    private static readonly EmbyCredentials Credentials = new("fixture-user-id", "fixture-access-token");
    public static async Task RunAsync()
    {
        await AuthenticationAsync();
        await CatalogAsync();
        await AssetsAsync();
        await RejectionsAsync();
        await CancellationAsync();
    }
    private static void Require(bool value, string context)
    { if (!value) throw new InvalidOperationException("Emby verification failed: " + context); }
    private static EmbyClient Client(Guid source, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        new(source, "Fixture Emby", "https://emby.example.invalid/proxy/", Credentials, () => new Handler(response));
    private static Task<HttpResponseMessage> Immediate(HttpResponseMessage response) => Task.FromResult(response);
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(field => field.Split('=', 2))
        .ToDictionary(field => Uri.UnescapeDataString(field[0]), field => Uri.UnescapeDataString(field.Length > 1 ? field[1] : ""));
    private static void AuthenticationHeaders(HttpRequestMessage request, bool hasToken = true)
    {
        Require(request.Headers.TryGetValues("Authorization", out var header) && header.Single().StartsWith("Emby ", StringComparison.Ordinal), "Emby device authorization header");
        Require(!request.RequestUri!.Query.Contains("fixture-access-token", StringComparison.Ordinal) &&
            !request.RequestUri.Query.Contains("api_key", StringComparison.OrdinalIgnoreCase), "token absent from URI");
        if (hasToken) Require(request.Headers.GetValues("X-Emby-Token").Single() == Credentials.AccessToken, "access token exclusively header");
        else Require(!request.Headers.Contains("X-Emby-Token"), "authentication has no old token");
    }
    private static async Task AuthenticationAsync()
    {
        Require(EmbyClient.NormalizeServerAddress("https://emby.example.invalid") == "https://emby.example.invalid/emby/", "root API prefix");
        Require(EmbyClient.NormalizeServerAddress("https://emby.example.invalid/proxy/emby/") == "https://emby.example.invalid/proxy/emby/", "existing API prefix retained");
        Require(EmbyClient.NormalizeServerAddress("https://emby.example.invalid/proxy/EMBY/") == "https://emby.example.invalid/proxy/emby/", "API prefix canonical case");
        Require(EmbyClient.NormalizeServerAddress("https://emby.example.invalid/proxy") == "https://emby.example.invalid/proxy/emby/", "reverse proxy retained");
        foreach (string address in new[] { string.Concat("https://", "user:secret@", "emby.example.invalid"), "https://emby.example.invalid/?api_key=secret", "file:///private", "https://emby.example.invalid/#secret" })
        { bool invalid = false; try { EmbyClient.NormalizeServerAddress(address); } catch (ArgumentException error) { invalid = !error.Message.Contains("secret", StringComparison.Ordinal); } Require(invalid, "unsafe server address rejected without reflection"); }
        int calls = 0;
        var credentials = await EmbyClient.AuthenticateAsync("https://emby.example.invalid/proxy/", "fixture-account", "fixture-password", default,
            () => new Handler(async (request, ct) =>
            {
                calls++; AuthenticationHeaders(request, false);
                var uri = request.RequestUri ?? throw new InvalidOperationException("Missing authentication request URI");
                var content = request.Content ?? throw new InvalidOperationException("Missing authentication request body");
                Require(request.Method == HttpMethod.Post && uri.AbsolutePath == "/proxy/emby/Users/AuthenticateByName", "authenticate endpoint");
                Require(string.IsNullOrEmpty(uri.Query), "credentials absent from authenticate query");
                using var body = JsonDocument.Parse(await content.ReadAsStringAsync(ct));
                Require(body.RootElement.GetProperty("Username").GetString() == "fixture-account" && body.RootElement.GetProperty("Pw").GetString() == "fixture-password", "authenticate JSON contract");
                return Json(new { User = new { Id = Credentials.UserId }, AccessToken = Credentials.AccessToken });
            }));
        Require(calls == 1 && credentials == Credentials && !credentials.ToString().Contains("fixture", StringComparison.Ordinal), "authenticated credentials redact diagnostic string");
        await FailsAsync(MusicFailure.Unauthorized, () => EmbyClient.AuthenticateAsync("https://emby.example.invalid", "fixture-account", "fixture-password", default,
            () => new Handler((_, _) => Immediate(new(HttpStatusCode.Forbidden) { Content = new StringContent("fixture-password") }))));
        await FailsAsync(MusicFailure.InvalidResponse, () => EmbyClient.AuthenticateAsync("https://emby.example.invalid", "fixture-account", "fixture-password", default,
            () => new Handler((_, _) => Immediate(Json(new { User = new { Id = "../elsewhere" }, AccessToken = "private" })))));
    }
    private static object Song(int index) => new
    {
        Id = "song-" + index, Name = "Song " + index, Type = "Audio", RunTimeTicks = 600000000L,
        Album = "Fixture Album", AlbumId = "album-one", Artists = new[] { "Fixture Artist" },
        ArtistItems = new[] { new { Id = "artist-one", Name = "Fixture Artist" } },
        ImageTags = new { Primary = "image-tag" }, UserData = new { IsFavorite = true }
    };
    private static async Task CatalogAsync()
    {
        Guid source = Guid.NewGuid(); List<int> offsets = []; int limit = 0;
        using var client = Client(source, (request, _) =>
        {
            AuthenticationHeaders(request);
            if (request.RequestUri!.AbsolutePath.EndsWith("/Users/" + Credentials.UserId, StringComparison.Ordinal)) return Immediate(Json(new { Id = Credentials.UserId }));
            Require(request.RequestUri.AbsolutePath == "/proxy/emby/Users/fixture-user-id/Items", "user scoped catalog");
            var query = Query(request); Require(query["Recursive"] == "true" && query["IncludeItemTypes"] == "Audio" && query["SortBy"] == "SortName,Id", "audio and stable ordering query");
            int offset = int.Parse(query["StartIndex"]); limit = int.Parse(query["Limit"]); offsets.Add(offset);
            return Immediate(Json(new { Items = Enumerable.Range(offset, offset == 0 ? limit : 2).Select(Song).ToArray(), TotalRecordCount = limit + 2, StartIndex = offset }));
        });
        await client.CheckConnectionAsync(); var songs = await client.LoadTracksAsync();
        Require(songs.Count == limit + 2 && offsets.SequenceEqual(new[] { 0, limit }), "all pages collected once");
        Require(songs.All(song => song.SourceInstanceId == source) && songs[0].DurationSeconds == 60 && songs[0].Artist == "Fixture Artist" &&
            songs[0].AlbumId == "album-one" && songs[0].CoverId == "song-0" && songs[0].IsFavorite, "scoped track metadata");
        using var other = Client(Guid.NewGuid(), (_, _) => Immediate(Json(new { Items = new[] { Song(0) }, TotalRecordCount = 1 })));
        Require((await other.LoadTracksAsync()).Single().Reference != songs[0].Reference, "equal track IDs across sources isolated");
        using var empty = Client(Guid.NewGuid(), (_, _) => Immediate(Json(new { Items = Array.Empty<object>(), TotalRecordCount = 0 })));
        Require((await empty.LoadTracksAsync()).Count == 0, "empty allowed catalog");
        using var optional = Client(source, (_, _) => Immediate(Json(new { Items = new[] { new
        { Id = "song-null", Name = "Unknown metadata", Type = "Audio", RunTimeTicks = (long?)null,
          AlbumId = "", Artists = (string[]?)null, ArtistItems = (object[]?)null, ImageTags = (object?)null, UserData = (object?)null } }, TotalRecordCount = 1 })));
        var unknown = (await optional.LoadTracksAsync()).Single();
        Require(unknown.DurationSeconds == 0 && unknown.AlbumId is null && unknown.CoverId is null && unknown.Artists.Count == 0, "nullable optional DTO metadata");
        using var manyArtists = Client(source, (_, _) => Immediate(Json(new { Items = new[] { new
        { Id = "song-artists", Type = "Audio", Artists = new[] { new string('A', 4096), "second" }, AlbumId = "album-two", AlbumPrimaryImageTag = "tag" } }, TotalRecordCount = 1 })));
        var combined = (await manyArtists.LoadTracksAsync()).Single();
        Require(combined.Artist.Length == 4096 && combined.CoverId == "album-two", "bounded combined artists and album artwork fallback");
        int repeats = 0;
        using var repeated = Client(Guid.NewGuid(), (request, _) =>
        { repeats++; int size = int.Parse(Query(request)["Limit"]); return Immediate(Json(new { Items = Enumerable.Range(0, size).Select(Song).ToArray(), TotalRecordCount = size + 2 })); });
        await FailsAsync(MusicFailure.InvalidResponse, () => repeated.LoadTracksAsync()); Require(repeats == 2, "repeated page rejected");
        int changingCalls = 0;
        using var changing = Client(Guid.NewGuid(), (_, _) =>
        { changingCalls++; return Immediate(Json(new { Items = new[] { Song(changingCalls) }, TotalRecordCount = changingCalls + 2 })); });
        await FailsAsync(MusicFailure.InvalidResponse, () => changing.LoadTracksAsync()); Require(changingCalls == 2, "changing total rejected");
        foreach (object payload in new object[]
        {
            new { Items = Array.Empty<object>(), TotalRecordCount = 10001 },
            new { Items = Array.Empty<object>(), TotalRecordCount = 1 },
            new { Items = new[] { Song(0) }, TotalRecordCount = 1, StartIndex = 9 },
            new { Items = new[] { new { Id = "unsafe/path", Type = "Audio" } }, TotalRecordCount = 1 },
            new { Items = new[] { new { Id = "song", Type = "Video" } }, TotalRecordCount = 1 },
            new { Items = new[] { new { Id = "song", Type = "Audio", RunTimeTicks = "bad" } }, TotalRecordCount = 1 },
            new { Items = Array.Empty<object>(), TotalRecordCount = "bad" }
        })
        { using var invalid = Client(Guid.NewGuid(), (_, _) => Immediate(Json(payload))); await FailsAsync(MusicFailure.InvalidResponse, () => invalid.LoadTracksAsync()); }
        using var mismatch = Client(Guid.NewGuid(), (_, _) => Immediate(Json(new { Id = "different-user" })));
        await FailsAsync(MusicFailure.InvalidResponse, () => mismatch.CheckConnectionAsync());
    }
    private static async Task AssetsAsync()
    {
        Guid source = Guid.NewGuid(); int requests = 0; byte[] audio = Enumerable.Range(0, 300000).Select(index => (byte)(index % 251)).ToArray();
        using var client = Client(source, (request, _) =>
        {
            requests++; AuthenticationHeaders(request);
            if (request.RequestUri!.AbsolutePath.EndsWith("Images/Primary", StringComparison.Ordinal))
            {
                Require(request.RequestUri.AbsolutePath == "/proxy/emby/Items/cover-one/Images/Primary", "cover endpoint");
                var image = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                image.Content.Headers.ContentType = new("image/png"); return Immediate(image);
            }
            Require(request.RequestUri.AbsolutePath == "/proxy/emby/Audio/song-one/stream" && Query(request)["static"] == "true", "original audio stream query");
            var range = request.Headers.Range!.Ranges.Single(); long from = range.From!.Value, to = range.To!.Value;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(audio.AsSpan((int)from, (int)(to - from + 1)).ToArray()) };
            response.Content.Headers.ContentRange = new(from, to, audio.Length); response.Content.Headers.ContentType = new("audio/flac");
            response.Headers.ETag = new("\"fixture-v1\""); return Immediate(response);
        });
        var song = new MusicTrack("song-one", "Fixture", "Artist", 60, false) { SourceInstanceId = source, CoverId = "cover-one" };
        Require((await client.ReadCoverAsync(song)).SequenceEqual(new byte[] { 1, 2, 3 }), "cover data bounded response");
        Require((await client.ReadLyricsAsync(song)).Lines.Count == 0 && !client.Source.Capabilities.HasFlag(MusicSourceCapabilities.Lyrics), "unimplemented lyric capability not advertised");
        using var stream = await client.OpenPlaybackAsync(song.Reference); Require(stream.CanSeek && stream.Length == audio.Length, "seekable range stream");
        byte[] first = new byte[10]; await stream.ReadExactlyAsync(first); Require(first.SequenceEqual(audio.Take(10)), "first playback bytes");
        stream.Seek(280000, SeekOrigin.Begin); byte[] last = new byte[5]; await stream.ReadExactlyAsync(last); Require(last.SequenceEqual(audio.Skip(280000).Take(5)), "range seek bytes");
        int before = requests;
        await FailsAsync(MusicFailure.Rejected, () => client.OpenPlaybackAsync(new(Guid.NewGuid(), song.Id)));
        await FailsAsync(MusicFailure.Rejected, () => client.ReadCoverAsync(song with { SourceInstanceId = Guid.NewGuid() }));
        await FailsAsync(MusicFailure.Rejected, () => client.OpenPlaybackAsync(new(source, "../other")));
        Require(requests == before, "source isolation before network");
        using var missing = Client(source, (_, _) => Immediate(new(HttpStatusCode.NotFound)));
        Require((await missing.ReadCoverAsync(song)).Length == 0, "missing artwork fallback");
        using var invalidImage = Client(source, (_, _) => Immediate(Json(new { privateMessage = "fixture-access-token" })));
        await FailsAsync(MusicFailure.InvalidResponse, () => invalidImage.ReadCoverAsync(song));
        using var oversized = Client(source, (_, _) =>
        { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; response.Content.Headers.ContentType = new("image/png"); response.Content.Headers.ContentLength = 4194305; return Immediate(response); });
        await FailsAsync(MusicFailure.InvalidResponse, () => oversized.ReadCoverAsync(song));
    }
    private static async Task RejectionsAsync()
    {
        Guid source = Guid.NewGuid(); var song = new MusicTrack("song", "Fixture", "Artist", 60, false) { SourceInstanceId = source, CoverId = "cover" };
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError })
        {
            using var client = Client(source, (_, _) => Immediate(new(status) { Content = new StringContent("fixture-access-token fixture-password private server") }));
            MusicFailure failure = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? MusicFailure.Unauthorized : MusicFailure.InvalidResponse;
            await FailsAsync(failure, () => client.CheckConnectionAsync()); await FailsAsync(failure, () => client.ReadCoverAsync(song)); await FailsAsync(failure, () => client.OpenPlaybackAsync(song.Reference));
        }
        int calls = 0;
        using var redirect = Client(source, (_, _) =>
        { calls++; var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri("https://elsewhere.example.invalid/private"); return Immediate(response); });
        await FailsAsync(MusicFailure.RedirectRejected, () => redirect.CheckConnectionAsync()); Require(calls == 1, "redirect not followed");
        using var malformed = Client(source, (_, _) => Immediate(new(HttpStatusCode.OK) { Content = new StringContent("fixture-access-token invalid JSON") }));
        await FailsAsync(MusicFailure.InvalidResponse, () => malformed.CheckConnectionAsync());
        using var network = Client(source, (_, _) => throw new HttpRequestException("fixture-access-token private URL"));
        await FailsAsync(MusicFailure.Unavailable, () => network.CheckConnectionAsync());
    }
    private static async Task CancellationAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var client = Client(Guid.NewGuid(), async (_, ct) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Json(new { Id = Credentials.UserId }); });
        var pending = client.LoadTracksAsync(cancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
        bool canceled = false; try { await pending; } catch (OperationCanceledException) { canceled = true; } Require(canceled, "caller cancellation propagated");
        using var preCanceled = new CancellationTokenSource(); preCanceled.Cancel();
        canceled = false; try { await client.ReadLyricsAsync(new("song", "Fixture", "Artist", 1, false) { SourceInstanceId = client.Source.Id }, preCanceled.Token); } catch (OperationCanceledException) { canceled = true; } Require(canceled, "lyric fallback cancellation");
        var disposed = Client(Guid.NewGuid(), (_, _) => throw new InvalidOperationException("network must not run")); disposed.Dispose();
        bool rejected = false; try { await disposed.CheckConnectionAsync(); } catch (ObjectDisposedException) { rejected = true; } Require(rejected, "disposed client stops request");
    }
    private static async Task FailsAsync(MusicFailure failure, Func<Task> action)
    {
        bool expected = false;
        try { await action(); } catch (MusicApiException error)
        { expected = error.Failure == failure && !error.ToString().Contains("fixture-access-token", StringComparison.Ordinal) && !error.ToString().Contains("fixture-password", StringComparison.Ordinal) && !error.Message.Contains("http", StringComparison.OrdinalIgnoreCase); }
        Require(expected, "sanitized failure " + failure);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken); }
}
