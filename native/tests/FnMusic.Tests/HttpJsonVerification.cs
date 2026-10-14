using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;

internal static class HttpJsonVerification
{
    public static async Task RunAsync()
    {
        Definition();
        await CatalogAsync();
        await CatalogRejectionsAsync();
        await AssetsAsync();
        await FailuresAsync();
        await CancellationAsync();
        Console.WriteLine("HTTP JSON protocol: 6 groups passed");
    }
    private static void Require(bool value, string context)
    { if (!value) throw new InvalidOperationException("HTTP JSON verification failed: " + context); }
    private static HttpJsonClient Client(Guid id, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        HttpJsonDefinition? definition = null) => new(id, "JSON fixture", "https://json.example.invalid/music/", definition ?? new(), () => new Handler(handler));
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static Task<HttpResponseMessage> Immediate(HttpResponseMessage response) => Task.FromResult(response);
    private static object Row(object id, string url = "audio.wav") => new { id, title = "Mapped song", artist = "Artist", album = "Album", duration = 60.5, url, cover = "cover.png", lyrics = "song.lrc" };
    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);
    private static void Definition()
    {
        var value = new HttpJsonDefinition { ListPath = " catalog/list ", ItemsPath = " songs ", DurationUnit = " MILLISECONDS " };
        var normalized = HttpJsonDefinition.Normalize(value);
        Require(HttpJsonDefinition.Parse(value.ToJson()) == normalized && normalized.ListPath == "catalog/list" && normalized.DurationUnit == "milliseconds", "canonical mapping roundtrip");
        Require(HttpJsonDefinition.Parse("{}") == new HttpJsonDefinition(), "defaults preserve versioned profile");
        foreach (string body in new[] { "null", "[]", "{\"headers\":{}}", "{\"version\":2}", "{\"Version\":1,\"version\":1}",
            "{\"listPath\":\"../songs\"}", "{\"listPath\":\"https://other.example.invalid\"}", "{\"listPath\":\"api/tracks?q=1\"}",
            "{\"idPath\":\"items[0]\"}", "{\"pageSize\":1001}", "{\"pageParameter\":\"size\"}", "{\"durationUnit\":\"minutes\"}" })
        {
            bool rejected = false; try { HttpJsonDefinition.Parse(body); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "invalid definitions rejected");
        }
        Require(HttpJsonClient.NormalizeServerAddress(" https://json.example.invalid/reverse/music ") == "https://json.example.invalid/reverse/music/", "reverse proxy root preserved");
        foreach (string address in new[] { "file:///music", "https://json.example.invalid/?session=private", "https://json.example.invalid/#fragment",
            string.Concat("https://", "user:pass@", "json.example.invalid/") })
        {
            bool rejected = false; try { HttpJsonClient.NormalizeServerAddress(address); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "server credentials and non-HTTP addresses rejected");
        }
    }
    private static async Task CatalogAsync()
    {
        Guid id = Guid.NewGuid(); List<int> pages = [];
        using var client = Client(id, (request, _) =>
        {
            Require(request.RequestUri!.AbsolutePath == "/music/api/tracks" && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "anonymous deployment route");
            var query = Query(request); Require(query["size"] == "2", "bounded configured page size");
            int page = int.Parse(query["page"]); pages.Add(page);
            return Immediate(Json(new { data = new { total = 3, items = page == 1 ? new[] { Row("one"), Row(2) } : new[] { Row("three") } } }));
        }, new() { PageSize = 2 });
        await client.CheckConnectionAsync(); var tracks = await client.LoadTracksAsync();
        Require(pages.SequenceEqual(new[] { 1, 1, 2 }) && tracks.Count == 3 && tracks[1].Id == "2", "complete pagination and stable integer ID");
        Require(tracks.All(track => track.SourceInstanceId == id && track.SourceLabel == "JSON fixture") && tracks[0].CoverId == "one" &&
            tracks[0].Title == "Mapped song" && tracks[0].Artist == "Artist" && tracks[0].Album == "Album" && tracks[0].DurationSeconds == 60.5, "mapped metadata and source identity");
        Require(!JsonSerializer.Serialize(tracks).Contains("https://", StringComparison.Ordinal) && !JsonSerializer.Serialize(tracks).Contains("audio.wav", StringComparison.Ordinal), "media URLs absent from persistent track DTO");
        using var custom = Client(Guid.NewGuid(), (_, _) => Immediate(Json(new[] { new { key = "one", metadata = new { seconds = 2000 }, stream = "/audio.wav" } })),
            new() { ItemsPath = "", TotalPath = "", IdPath = "key", PlayUrlPath = "stream", DurationPath = "metadata.seconds", DurationUnit = "milliseconds" });
        var bare = (await custom.LoadTracksAsync()).Single();
        Require(bare.DurationSeconds == 2 && bare.Title == "未命名曲目" && bare.Artist == "" && bare.Album == "" && bare.CoverId is null && bare.Reference != tracks[0].Reference, "optional fields, nested mapping, root array and cross-source isolation");
    }
    private static async Task CatalogRejectionsAsync()
    {
        foreach (string url in new[] { "https://other.example.invalid/audio.wav", "http://json.example.invalid/audio.wav", "//other.example.invalid/audio.wav",
            "audio.wav?session=private", "audio.wav#fragment", string.Concat("https://", "user:pass@", "json.example.invalid/audio.wav"), "file:///audio.wav" })
        {
            int calls = 0;
            using var client = Client(Guid.NewGuid(), (_, _) => { calls++; return Immediate(Json(new { data = new { total = 1, items = new[] { Row("one", url) } } })); });
            await FailsAsync(MusicFailure.InvalidResponse, () => client.LoadTracksAsync()); Require(calls == 1, "unsafe media URL rejected before requesting media");
        }
        var invalidRows = new object[] { new { id = -1, url = "audio.wav" }, new { id = 1.25, url = "audio.wav" }, new { id = "", url = "audio.wav" },
            new { id = "one" }, new { id = "one", url = "audio.wav", duration = -1 }, new { id = "one", url = "audio.wav", title = 1 },
            new { id = "one", url = "audio.wav", cover = "https://other.example.invalid/cover.png" }, new { id = "one", url = "audio.wav", lyrics = "song.lrc?q=private" } };
        foreach (var row in invalidRows)
        {
            using var client = Client(Guid.NewGuid(), (_, _) => Immediate(Json(new { data = new { total = 1, items = new[] { row } } })));
            await FailsAsync(MusicFailure.InvalidResponse, () => client.LoadTracksAsync());
        }
        foreach (int mode in Enumerable.Range(0, 5))
        {
            int calls = 0;
            using var client = Client(Guid.NewGuid(), (_, _) =>
            {
                calls++;
                return Immediate(mode switch
                {
                    0 => Json(new { data = new { total = calls == 1 ? 3 : 4, items = new[] { Row("id-" + calls), Row("last-" + calls) } } }),
                    1 => Json(new { data = new { total = 3, items = new[] { Row("one") } } }),
                    2 => Json(new { data = new { total = 10001, items = Array.Empty<object>() } }),
                    3 => Json(new { data = new { items = new[] { Row("one"), Row("two") } } }),
                    _ => Json(new { data = new { total = 1, items = new[] { Row("one"), Row("two") } } })
                });
            }, new() { PageSize = 2 });
            await FailsAsync(MusicFailure.InvalidResponse, () => client.LoadTracksAsync());
            Require(calls <= 2, "inconsistent counts and repeated pages rejected promptly");
        }
        int generation = 0;
        using var stable = Client(Guid.NewGuid(), (request, _) => request.RequestUri!.AbsolutePath.EndsWith("tracks", StringComparison.Ordinal)
            ? Immediate(Json(new { data = new { total = 1, items = new[] { Row("stable", generation == 0 ? "audio.wav" : "https://other.example.invalid/audio.wav") } } }))
            : Immediate(Range(request, [1, 2, 3])));
        var saved = (await stable.LoadTracksAsync()).Single(); generation++;
        await FailsAsync(MusicFailure.InvalidResponse, () => stable.LoadTracksAsync());
        using var stream = await stable.OpenAsync(saved.Reference); Require(stream.Length == 3, "failed refresh retains complete prior asset mapping");
    }
    private static HttpResponseMessage Range(HttpRequestMessage request, byte[] audio)
    {
        var range = request.Headers.Range!.Ranges.Single(); int from = checked((int)range.From!.Value), to = checked((int)range.To!.Value);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(audio[from..(to + 1)]) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, audio.Length);
        response.Content.Headers.ContentType = new("audio/wav"); response.Headers.ETag = new("\"fixture-audio\""); return response;
    }
    private static async Task AssetsAsync()
    {
        int lyricMode = 0, coverMode = 0, calls = 0; byte[] audio = Enumerable.Range(0, 128).Select(index => (byte)index).ToArray();
        var client = Client(Guid.NewGuid(), (request, _) =>
        {
            calls++; string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("tracks", StringComparison.Ordinal)) return Immediate(Json(new { data = new { total = 1, items = new[] { Row("one") } } }));
            if (path.EndsWith("audio.wav", StringComparison.Ordinal)) return Immediate(Range(request, audio));
            if (path.EndsWith("cover.png", StringComparison.Ordinal))
            {
                if (coverMode == 1) return Immediate(new(HttpStatusCode.NotFound));
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(coverMode == 2 ? new byte[4 * 1024 * 1024 + 1] : [137, 80, 78, 71]) };
                response.Content.Headers.ContentType = new("image/png"); return Immediate(response);
            }
            return Immediate(lyricMode switch
            {
                1 => new(HttpStatusCode.NotImplemented),
                2 => new(HttpStatusCode.OK) { Content = new ByteArrayContent([255, 255]) },
                3 => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[512001]) },
                _ => new(HttpStatusCode.OK) { Content = new StringContent("[00:01.00]first\n[00:02.00]second", Encoding.UTF8) }
            });
        });
        var track = (await client.LoadTracksAsync()).Single();
        Require((await client.ReadCoverAsync(track))!.SequenceEqual(new byte[] { 137, 80, 78, 71 }), "cover bytes");
        coverMode = 1; Require(await client.ReadCoverAsync(track) is null, "missing optional cover");
        coverMode = 2; await FailsAsync(MusicFailure.InvalidResponse, () => client.ReadCoverAsync(track));
        var lyrics = await client.GetLyricsAsync(track); Require(lyrics.IsTimed && lyrics.Lines.Count == 2 && lyrics.FindLine(1.5) == 0, "timed UTF8 LRC");
        lyricMode = 1; Require((await client.GetLyricsAsync(track)).Lines.Count == 0, "unsupported lyrics optional");
        lyricMode = 2; await FailsAsync(MusicFailure.InvalidResponse, () => client.GetLyricsAsync(track));
        lyricMode = 3; await FailsAsync(MusicFailure.InvalidResponse, () => client.GetLyricsAsync(track));
        int before = calls; await FailsAsync(MusicFailure.Rejected, () => client.OpenAsync(new(Guid.NewGuid(), track.Id)));
        await FailsAsync(MusicFailure.Rejected, () => client.OpenAsync(new(client.Source.Id, "unknown"))); Require(calls == before, "invalid identities cause no network requests");
        using var stream = await client.OpenAsync(track.Reference); client.Dispose();
        stream.Seek(50, SeekOrigin.Begin); byte[] bytes = new byte[8]; await stream.ReadExactlyAsync(bytes);
        Require(bytes.SequenceEqual(audio[50..58]), "independent Range stream survives catalog disposal");
    }
    private static async Task FailuresAsync()
    {
        foreach (var pair in new[] { (HttpStatusCode.Unauthorized, MusicFailure.Unauthorized), (HttpStatusCode.Forbidden, MusicFailure.Unauthorized),
            (HttpStatusCode.Redirect, MusicFailure.RedirectRejected), (HttpStatusCode.InternalServerError, MusicFailure.Unavailable) })
        {
            using var client = Client(Guid.NewGuid(), (_, _) => Immediate(new(pair.Item1) { Content = new StringContent("private response text") }));
            await FailsAsync(pair.Item2, () => client.LoadTracksAsync());
        }
        using var malformed = Client(Guid.NewGuid(), (_, _) => Immediate(new(HttpStatusCode.OK) { Content = new StringContent("not JSON private response") }));
        await FailsAsync(MusicFailure.InvalidResponse, () => malformed.LoadTracksAsync());
        using var oversized = Client(Guid.NewGuid(), (_, _) => Immediate(new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]) }));
        await FailsAsync(MusicFailure.InvalidResponse, () => oversized.LoadTracksAsync());
        using var unavailable = Client(Guid.NewGuid(), (_, _) => throw new HttpRequestException("private transport URL"));
        await FailsAsync(MusicFailure.Unavailable, () => unavailable.LoadTracksAsync());
    }
    private static async Task CancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = Client(Guid.NewGuid(), async (_, ct) => { cancellation.Cancel(); await Task.Delay(1000, ct); return Json(new { }); });
        bool canceled = false; try { await client.LoadTracksAsync(cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "caller cancellation is preserved");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = Client(Guid.NewGuid(), async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Json(new { }); });
        Task work = disposed.LoadTracksAsync(); await entered.Task; disposed.Dispose();
        canceled = false; try { await work; } catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "catalog disposal cancels in-flight request");
    }
    private static async Task FailsAsync(MusicFailure expected, Func<Task> work)
    {
        bool failed = false; try { await work(); } catch (MusicApiException error)
        { failed = true; Require(error.Failure == expected && error.Message == expected.ToString() && error.InnerException is null, "sanitized error category"); }
        Require(failed, "expected rejection");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken); }
}
