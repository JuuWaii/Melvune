using System.Net;
using System.Text;
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;

internal static class TrackCatalogVerification
{
    internal static async Task CompleteAsync()
    {
        foreach (int count in new[] { 0, 49, 50, 51, 273, 357 })
        {
            var requested = new List<int>(); var id = Guid.NewGuid();
            using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), new FakeHandler(request =>
            {
                var query = Query(request); int page = int.Parse(query["page"]); requested.Add(page);
                Require(query["size"] == "50");
                return Response(count, Enumerable.Range(0, count).Skip((page - 1) * 50).Take(50));
            }), id);
            var tracks = await TrackCatalogReader.ReadAsync((page, ct) => api.ListTracksAsync(page, 50, ct), default);
            Require(tracks.Count == count && tracks.Select(t => t.Id).SequenceEqual(Enumerable.Range(0, count).Select(i => "track-" + i))
                && tracks.All(t => t.SourceInstanceId == id) && requested.SequenceEqual(Enumerable.Range(1, Math.Max(1, (count + 49) / 50))));
        }
    }
    internal static async Task InconsistentAsync()
    {
        foreach (string mode in new[] { "duplicate", "changed", "short", "overflow" })
        {
            int calls = 0;
            using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), new FakeHandler(_ =>
            {
                calls++;
                int count = mode == "overflow" ? TrackCatalogReader.MaximumTracks + 1 : calls > 1 && mode == "changed" ? 101 : 100;
                var indices = Enumerable.Range(calls > 1 && mode != "duplicate" ? 50 : 0, calls > 1 && mode == "short" ? 49 : 50);
                return Response(count, indices);
            }), Guid.NewGuid());
            try { await TrackCatalogReader.ReadAsync((page, ct) => api.ListTracksAsync(page, 50, ct), default); throw new Exception("Partial catalog accepted"); }
            catch (MusicApiException ex) { Require(ex.Failure == MusicFailure.InvalidResponse && calls <= 2); }
        }
    }
    internal static async Task CancelAsync()
    {
        int calls = 0; using var cancelled = new CancellationTokenSource();
        var id = Guid.NewGuid();
        async Task<TrackPage> Read(int page, CancellationToken ct)
        {
            calls++;
            if (page == 2) { cancelled.Cancel(); await Task.Delay(Timeout.Infinite, ct); }
            return new(Enumerable.Range(0, 50).Select(i => new MusicTrack(i.ToString(), "fixture", "", 0, false) { SourceInstanceId = id }).ToArray(), 100);
        }
        try { await TrackCatalogReader.ReadAsync(Read, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Require(calls == 2); }
        calls = 0;
        try { await TrackCatalogReader.ReadAsync(Read, cancelled.Token); throw new Exception("Pre-cancellation ignored"); }
        catch (OperationCanceledException) { Require(calls == 0); }
        using var progressCancel = new CancellationTokenSource(); calls = 0;
        try
        {
            await TrackCatalogReader.ReadAsync((_, _) =>
            {
                calls++;
                return Task.FromResult(new TrackPage(Enumerable.Range(0, 50).Select(i =>
                    new MusicTrack(i.ToString(), "fixture", "", 0, false) { SourceInstanceId = id }).ToArray(), 100));
            }, progressCancel.Token, new CancellingProgress(progressCancel));
            throw new Exception("Progress cancellation ignored");
        }
        catch (OperationCanceledException) { Require(calls == 1); }
    }
    private sealed class CancellingProgress(CancellationTokenSource source) : IProgress<int>
    { public void Report(int value) => source.Cancel(); }
    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?').Split('&')
        .Select(value => value.Split('=', 2)).ToDictionary(value => value[0], value => value[1]);
    private static HttpResponseMessage Response(int count, IEnumerable<int> indices) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { code = 0, data = new { total = count,
        list = indices.Select(i => new { guid = "track-" + i, title = "fixture " + i, duration = 60000 }) } }), Encoding.UTF8, "application/json") };
    private static void Require(bool value) { if (!value) throw new Exception("Catalog assertion failed"); }
}
