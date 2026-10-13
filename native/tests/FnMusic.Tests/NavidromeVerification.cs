using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;
using FnMusic.Windows;

internal static class NavidromeVerification
{
    public static async Task RunAsync()
    {
        await CatalogAsync();
        await RejectionsAsync();
        await PlaybackAssetsAsync();
        await StoreAsync();
        await EncryptedRestartAsync();
    }

    private static void Require(bool value, string context)
    { if (!value) throw new InvalidOperationException("Navidrome verification failed: " + context); }
    private static NavidromeCredentials Credentials => NavidromeClient.CreateCredentials("fixture-user", "fixture-password");
    private static NavidromeClient Client(Guid source, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        new(source, "Fixture Navidrome", "https://navidrome.example.invalid/server/", Credentials, () => new Handler(response));
    private static Task<HttpResponseMessage> Immediate(HttpResponseMessage response) => Task.FromResult(response);
    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Ok(object? search = null)
    {
        var body = new Dictionary<string, object?> { ["status"] = "ok", ["version"] = "1.16.1" };
        if (search is not null) body["searchResult3"] = search;
        return Json(new Dictionary<string, object?> { ["subsonic-response"] = body });
    }
    private static async Task CatalogAsync()
    {
        var offsets = new List<int>(); int pageSize = 0;
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        using var client = Client(first, (request, _) =>
        {
            var query = Query(request); Require(query["u"] == "fixture-user" && query.ContainsKey("t") && query.ContainsKey("s") && !query.ContainsKey("p"), "token authentication");
            Require(query["t"] == Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("fixture-password" + query["s"]))) && query["v"] == "1.16.1" && query["f"] == "json", "protocol token algorithm and JSON envelope");
            Require(request.RequestUri!.AbsolutePath.StartsWith("/server/rest/", StringComparison.Ordinal), "server path retained");
            if (request.RequestUri.AbsolutePath.Contains("ping")) return Immediate(Ok());
            int offset = int.Parse(query["songOffset"]), count = int.Parse(query["songCount"]);
            Require(count is > 0 and <= 1000, "bounded page size"); pageSize = count; offsets.Add(offset);
            int rows = offset == 0 ? count : offset == count ? 2 : 0;
            var songs = Enumerable.Range(offset, rows).Select(index => new
            { id = "song-" + index, title = "Title " + index, artist = "Fixture artist", album = "Fixture album", duration = 60 + index, coverArt = "cover-" + index, albumId = "album-one" }).ToArray();
            return Immediate(Ok(new { song = songs }));
        });
        await client.CheckConnectionAsync(default);
        var tracks = await client.LoadTracksAsync(default);
        Require(tracks.Count == pageSize + 2 && offsets.SequenceEqual(new[] { 0, pageSize }), "complete paginated catalog");
        Require(tracks.All(track => track.SourceInstanceId == first) && tracks.Last().Id == "song-" + (pageSize + 1), "persistent identity on final row");
        Require(tracks[0].Title == "Title 0" && tracks[0].Artist == "Fixture artist" && tracks[0].Album == "Fixture album" && tracks[0].DurationSeconds == 60 && tracks[0].CoverId == "cover-0", "metadata mapping");
        using var other = Client(second, (_, _) => Immediate(Ok(new { song = new[] { new { id = "song-0", title = "Other", duration = 2 } } })));
        var otherTrack = (await other.LoadTracksAsync(default)).Single();
        Require(otherTrack.Id == tracks[0].Id && otherTrack.Reference != tracks[0].Reference, "cross-source same ID isolation");
        int repeatedCalls = 0;
        using var repeated = Client(Guid.NewGuid(), (request, _) =>
        {
            repeatedCalls++; int count = int.Parse(Query(request)["songCount"]);
            return Immediate(Ok(new { song = Enumerable.Range(0, count).Select(index => new { id = "repeat-" + index, title = "Repeated", duration = 1 }).ToArray() }));
        });
        await FailsAsync(MusicFailure.InvalidResponse, () => repeated.LoadTracksAsync(default));
        Require(repeatedCalls == 2, "repeated pages rejected before further requests");
        using var empty = Client(Guid.NewGuid(), (_, _) => Immediate(Ok()));
        Require((await empty.LoadTracksAsync(default)).Count == 0, "empty authorized catalog remains valid");
    }

    private static async Task RejectionsAsync()
    {
        using var denied = Client(Guid.NewGuid(), (_, _) => Immediate(ProtocolError(40, "fixture-password private server failure")));
        await FailsAsync(MusicFailure.Unauthorized, () => denied.CheckConnectionAsync(default));
        int redirects = 0;
        using var redirect = Client(Guid.NewGuid(), (_, _) =>
        {
            redirects++; var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://elsewhere.example.invalid/private"); return Immediate(response);
        });
        await FailsAsync(MusicFailure.RedirectRejected, () => redirect.CheckConnectionAsync(default));
        Require(redirects == 1, "redirect never followed");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var delayed = Client(Guid.NewGuid(), async (_, ct) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Ok(); });
        var pending = delayed.LoadTracksAsync(cancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
        bool canceled = false; try { await pending; } catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "catalog caller cancellation propagated");
        foreach (var response in new Func<HttpResponseMessage>[]
        {
            () => new(HttpStatusCode.Unauthorized),
            () => new(HttpStatusCode.OK) { Content = new StringContent("not JSON fixture-password", Encoding.UTF8, "application/json") },
            () => Json(new Dictionary<string, object> { ["subsonic-response"] = new { status = "failed", error = new { code = 0, message = "fixture-password" } } })
        })
        {
            using var invalid = Client(Guid.NewGuid(), (_, _) => Immediate(response()));
            bool sanitized = false;
            try { await invalid.CheckConnectionAsync(default); }
            catch (MusicApiException ex)
            { sanitized = !ex.ToString().Contains("fixture-password", StringComparison.Ordinal) && !ex.ToString().Contains("example.invalid", StringComparison.Ordinal); }
            Require(sanitized, "HTTP malformed or rejected responses exclude private details");
        }
    }

    private static async Task PlaybackAssetsAsync()
    {
        Guid id = Guid.NewGuid(); var song = new MusicTrack("song-one", "Fixture", "Artist", 60, false) { SourceInstanceId = id, CoverId = "cover-one" };
        byte[] audio = Enumerable.Range(0, 600000).Select(index => (byte)(index % 251)).ToArray();
        byte[] cover = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var ranges = new List<long>(); bool noLyrics = false; int requests = 0;
        using var client = Client(id, (request, _) =>
        {
            requests++; string route = request.RequestUri!.AbsolutePath;
            if (route.Contains("stream") || route.Contains("download"))
            {
                Require(Query(request)["id"] == song.Id, "playback song identity");
                var range = request.Headers.Range!.Ranges.Single(); long start = range.From!.Value, end = range.To!.Value; ranges.Add(start);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(audio[(int)start..((int)end + 1)]) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, audio.Length); response.Headers.ETag = new EntityTagHeaderValue("\"fixture-version\"");
                return Immediate(response);
            }
            if (route.Contains("getCoverArt"))
            { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cover) }; response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png"); return Immediate(response); }
            if (route.Contains("getLyricsBySongId"))
                return Immediate(noLyrics ? ProtocolError(70, "not supported") : Json(new Dictionary<string, object> { ["subsonic-response"] = new { status = "ok", lyricsList = new { structuredLyrics = new[] { new { synced = true, offset = 0, line = new[] { new { start = 0, value = "First line" }, new { start = 2500, value = "Second line" } } } } } } }));
            if (route.Contains("getLyrics")) return Immediate(Ok());
            throw new InvalidOperationException("Unexpected fixture route");
        });
        using (var stream = await client.OpenAsync(song.Reference, default))
        {
            Require(stream.CanSeek && stream.Length == audio.Length, "seekable original stream");
            byte[] buffer = new byte[37]; await stream.ReadExactlyAsync(buffer);
            Require(buffer.SequenceEqual(audio[..37]), "initial audio bytes");
            stream.Seek(400000, SeekOrigin.Begin); await stream.ReadExactlyAsync(buffer);
            Require(buffer.SequenceEqual(audio[400000..400037]) && ranges.Contains(262144), "seek fetches later validated range");
        }
        using var artwork = await client.OpenArtworkAsync(song, 256, default);
        Require(artwork is not null, "artwork returned");
        using var image = new MemoryStream(); await artwork!.CopyToAsync(image); Require(image.ToArray().SequenceEqual(cover), "cover bytes");
        var lyrics = await client.GetLyricsAsync(song, default);
        Require(lyrics.IsTimed && lyrics.Lines.Count == 2 && lyrics.Lines[1].Seconds == 2.5 && lyrics.Lines[1].Text == "Second line", "structured milliseconds mapping");
        noLyrics = true; Require((await client.GetLyricsAsync(song, default)).Lines.Count == 0, "unsupported lyric endpoint leaves embedded fallback available");
        int prior = requests;
        await FailsAsync(MusicFailure.Rejected, () => client.OpenAsync(song.Reference with { SourceInstanceId = Guid.NewGuid() }, default));
        Require(requests == prior, "foreign source sends no requests");
        using var absent = Client(id, (_, _) => Immediate(new(HttpStatusCode.NotFound)));
        Require(await absent.OpenArtworkAsync(song, 256, default) is null && (await absent.GetLyricsAsync(song, default)).Lines.Count == 0, "optional asset HTTP not-found preserves playback");
        using var denied = Client(id, (_, _) => Immediate(new(HttpStatusCode.Forbidden)));
        await FailsAsync(MusicFailure.Unauthorized, () => denied.OpenArtworkAsync(song, 256, default));
        await FailsAsync(MusicFailure.Unauthorized, () => denied.GetLyricsAsync(song, default));
        using var oversized = Client(id, (request, _) =>
        {
            var response = Ok(); response.Content.Headers.ContentLength = 9 * 1024 * 1024;
            if (request.RequestUri!.AbsolutePath.Contains("getCoverArt")) response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Immediate(response);
        });
        await FailsAsync(MusicFailure.InvalidResponse, () => oversized.CheckConnectionAsync(default));
        await FailsAsync(MusicFailure.InvalidResponse, () => oversized.OpenArtworkAsync(song, 256, default));
        foreach (int offset in new[] { 500, -500 })
        {
            using var shifted = Client(id, (_, _) => Immediate(Json(new Dictionary<string, object>
            { ["subsonic-response"] = new { status = "ok", lyricsList = new { structuredLyrics = new[] { new { synced = true, offset, line = new[] { new { start = 2500, value = "Shifted" } } } } } } })));
            Require((await shifted.GetLyricsAsync(song, default)).Lines.Single().Seconds == (2500 - offset) / 1000d, "structured positive and negative lyric offsets");
        }
    }

    private static async Task StoreAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "fnmusic-nav-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vault = new Vault(); var store = new NavidromeConnectionStore(directory, vault);
            var connection = new NavidromeConnection(Guid.NewGuid(), "First", "https://navidrome.example.invalid/base", "fixture-user");
            var credentials = Credentials; await store.SaveAsync(connection, credentials);
            var restart = new NavidromeConnectionStore(directory, vault);
            var loaded = (await restart.LoadAsync()).Single(); Require(loaded.SourceId == connection.SourceId && loaded.ServerAddress.EndsWith("/base/"), "normalized persistent source identity");
            Require(await restart.LoadCredentialsAsync(connection.SourceId) == credentials && vault.Values.Count == 1, "credentials delegated and restored");
            string index = Path.Combine(directory, "navidrome-connections.json"), original = await File.ReadAllTextAsync(index);
            Require(!original.Contains(credentials.Token, StringComparison.Ordinal) && !original.Contains(credentials.Salt, StringComparison.Ordinal) && !original.Contains("fixture-password", StringComparison.Ordinal), "public index excludes credentials");
            vault.FailSave = true; bool failed = false;
            try { await store.SaveAsync(connection with { Name = "Failed replacement" }, Credentials); } catch (IOException) { failed = true; }
            vault.FailSave = false;
            Require(failed && await File.ReadAllTextAsync(index) == original && await store.LoadCredentialsAsync(connection.SourceId) == credentials && vault.Values.Count == 1, "failed vault replacement preserves committed index and old slot");
            using (var cancellation = new CancellationTokenSource())
            {
                vault.Saved = cancellation.Cancel; bool canceled = false;
                try { await store.SaveAsync(connection with { Name = "Canceled replacement" }, Credentials, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                vault.Saved = null;
                Require(canceled && await File.ReadAllTextAsync(index) == original && await store.LoadCredentialsAsync(connection.SourceId) == credentials && vault.Values.Count == 1, "cancel after vault save preserves prior committed connection");
            }
            bool identityRejected = false;
            try { await store.SaveAsync(connection with { Username = "another-user" }, NavidromeClient.CreateCredentials("another-user", "other-password")); }
            catch (InvalidOperationException) { identityRejected = true; }
            Require(identityRejected && await File.ReadAllTextAsync(index) == original, "account switch requires new identity");
            bool duplicateRejected = false;
            try { await store.SaveAsync(connection with { SourceId = Guid.NewGuid() }, credentials); } catch (InvalidOperationException) { duplicateRejected = true; }
            Require(duplicateRejected, "duplicate account rejected");
            bool serverRejected = false;
            try { await store.SaveAsync(connection with { ServerAddress = "https://different.example.invalid/" }, credentials); }
            catch (InvalidOperationException) { serverRejected = true; }
            Require(serverRejected, "server switch requires new identity");
            await store.SaveAsync(connection with { Name = "Renamed" }, credentials);
            Require((await restart.LoadAsync()).Single().Name == "Renamed" && vault.Values.Count == 1, "committed edit cleans prior credential slot");
            vault.FailClear = true; await store.RemoveAsync(connection.SourceId);
            Require((await restart.LoadAsync()).Count == 0 && await restart.LoadCredentialsAsync(connection.SourceId) is null, "cleanup failure cannot resurrect removed connection");
            vault.FailClear = false; await store.RemoveAsync(connection.SourceId);
            var left = connection with { SourceId = Guid.NewGuid(), Username = "left-user" };
            var right = connection with { SourceId = Guid.NewGuid(), Username = "right-user" };
            await Task.WhenAll(store.SaveAsync(left, NavidromeClient.CreateCredentials(left.Username, "fixture-left")),
                restart.SaveAsync(right, NavidromeClient.CreateCredentials(right.Username, "fixture-right")));
            Require((await restart.LoadAsync()).Count == 2, "concurrent stores retain both committed connections");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task EncryptedRestartAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "fnmusic-nav-dpapi-" + Guid.NewGuid().ToString("N"));
        try
        {
            string sessions = Path.Combine(directory, "sessions"); var connection = new NavidromeConnection(Guid.NewGuid(), "Encrypted fixture", "https://encrypted.example.invalid", "fixture-user");
            var credentials = Credentials; await new NavidromeConnectionStore(directory, new DpapiSessionVault(sessions)).SaveAsync(connection, credentials);
            byte[] encrypted = await File.ReadAllBytesAsync(Directory.GetFiles(sessions, "*.bin").Single());
            Require(!Encoding.UTF8.GetString(encrypted).Contains(credentials.Token, StringComparison.Ordinal), "DPAPI vault does not store raw token");
            var restart = new NavidromeConnectionStore(directory, new DpapiSessionVault(sessions));
            Require(await restart.LoadCredentialsAsync(connection.SourceId) == credentials, "encrypted credentials survive store and vault reconstruction");
            await restart.RemoveAsync(connection.SourceId); Require(Directory.GetFiles(sessions, "*.bin").Length == 0, "encrypted slot removed");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static HttpResponseMessage ProtocolError(int code, string message) => Json(new Dictionary<string, object>
    { ["subsonic-response"] = new { status = "failed", error = new { code, message } } });
    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "");
    private static async Task FailsAsync(MusicFailure expected, Func<Task> work)
    {
        bool failed = false; try { await work(); } catch (MusicApiException ex)
        { failed = true; Require(ex.Failure == expected && ex.Message == expected.ToString() && ex.InnerException is null, "sanitized failure category"); }
        Require(failed, "expected API rejection");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => response(request, ct); }
    private sealed class Vault : ISessionVault
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool FailSave { get; set; }
        public bool FailClear { get; set; }
        public Action? Saved { get; set; }
        public Task<string?> LoadAsync(string key, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Values.GetValueOrDefault(key)); }
        public Task SaveAsync(string key, string token, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Values[key] = token; if (FailSave) throw new IOException("Injected vault failure"); Saved?.Invoke(); return Task.CompletedTask; }
        public Task ClearAsync(string key, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (FailClear) throw new IOException("Injected cleanup failure"); Values.Remove(key); return Task.CompletedTask; }
    }
}
