using System.Net;
using System.Text;
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;

internal static class MetadataCacheVerification
{
    private static void Require(bool value) { if (!value) throw new Exception("Metadata cache assertion failed."); }
    private static string FixtureDirectory() => Path.Combine(Path.GetTempPath(), "fnmusic-metadata-fixture", Guid.NewGuid().ToString("N"));
    private sealed class Handler : HttpMessageHandler
    {
        internal int Count = 55, Calls, LyricCalls, Covers, Writes;
        internal bool FailCollection, FailAssets, Unauthorized;
        internal CancellationTokenSource? CancelOnCollection;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; if (request.Method != HttpMethod.Get) Writes++;
            var route = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts[1]));
            if (Unauthorized) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            object Track(int index) => new { guid = "track-" + index, title = "title " + index, duration = 60000,
                artist = "artist", album = "album", coverId = "../unsafe/cover", albumGUID = "album" };
            object data;
            if (route.EndsWith("static/cover"))
            {
                Covers++; var content = new ByteArrayContent([1, 2, 3, 4]); content.Headers.ContentType = new("image/png");
                return Task.FromResult(new HttpResponseMessage(FailAssets ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = content });
            }
            if (route.EndsWith("lyric/list"))
            {
                LyricCalls++;
                if (FailAssets) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                data = new { list = new[] { new { guid = "lyric", content = "[00:00]cached\n[00:01]second", offset = 250 } } };
            }
            else if (route.EndsWith("playlist/list")) data = new { list = Count == 0 ? Array.Empty<object>() : new object[] { new { guid = "playlist", name = "playlist", updatedAt = 1 } } };
            else if (route.EndsWith("playlist/detail")) data = new { guid = "playlist", name = "playlist", updatedAt = 1 };
            else if (route.EndsWith("album/artist-detail/list")) data = new { total = 1, list = new[] { new { guid = "album", name = "collection", trackCount = Count, coverId = "../unsafe/cover" } } };
            else if (route.EndsWith("album/list") || route.EndsWith("artist/list"))
            {
                CancelOnCollection?.Cancel(); ct.ThrowIfCancellationRequested();
                if (FailCollection) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                data = new { total = Count == 0 ? 0 : 1, list = Count == 0 ? Array.Empty<object>() : new object[] { new { guid = route.EndsWith("album/list") ? "album" : "artist", name = "collection", trackCount = Count, coverId = "../unsafe/cover" } } };
            }
            else if (route.EndsWith("album/detail") || route.EndsWith("artist/detail")) data = new { guid = query["guid"], name = "collection", trackCount = Count, coverId = "../unsafe/cover" };
            else
            {
                int size = int.Parse(query["size"]), page = int.Parse(query["page"]);
                bool favorites = route.EndsWith("favorite-track/list"), playlist = route.EndsWith("track/playlist-detail/list");
                int total = favorites || playlist ? Math.Min(1, Count) : Count;
                data = new { total, list = Enumerable.Range(0, total).Skip((page - 1) * size).Take(size).Select(Track).ToArray() };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { code = 0, data }), Encoding.UTF8, "application/json") });
        }
    }
    internal static async Task PersistenceAsync()
    {
        string directory = FixtureDirectory(); var handler = new Handler(); Guid source = Guid.NewGuid();
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, source);
        api.RestoreSession("fixture-secret"); var cache = new NasMetadataCache(directory);
        var result = await NasMetadataSync.RunAsync(api, cache, default);
        Require(result.FailedAssets == 0 && result.Snapshot.Tracks.Count == 55 && result.Snapshot.Albums.Single().Tracks.Count == 55
            && result.Snapshot.Artists.Single().Tracks.Count == 55 && result.Snapshot.Playlists.Single().Tracks.Count == 1
            && result.Snapshot.Favorites.Single().Id == "track-0" && result.Snapshot.Tracks[0].IsFavorite && !result.Snapshot.Tracks[1].IsFavorite
            && handler.LyricCalls == 55 && handler.Covers == 1 && handler.Writes == 0);
        var restarted = new NasMetadataCache(directory);
        var loaded = await restarted.LoadAsync(source, default);
        Require(loaded is not null && loaded.Tracks.Select(t => t.Reference).SequenceEqual(result.Snapshot.Tracks.Select(t => t.Reference))
            && loaded.Albums.Single().Detail.CoverId == "../unsafe/cover" && loaded.Playlists.Single().Playlist.Reference.SourceInstanceId == source
            && loaded.Artists.Single().Albums.Single().Id == "album");
        var lyrics = await restarted.ReadLyricsAsync(loaded!.Tracks[0].Reference, default);
        Require(lyrics is { IsTimed: true } && lyrics.Lines[0].Seconds == .25 && lyrics.FindLine(1.3) == 1
            && (await restarted.ReadCoverAsync(source, "../unsafe/cover", NasCoverSize.Large, default))!.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Require(await restarted.LoadAsync(Guid.NewGuid(), default) is null
            && await restarted.ReadLyricsAsync(new(Guid.NewGuid(), "track-0"), default) is null
            && await restarted.ReadCoverAsync(Guid.NewGuid(), "../unsafe/cover", NasCoverSize.Large, default) is null);
        var documents = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        Require(documents.Length == 57 && documents.All(path => Path.GetRelativePath(directory, path).StartsWith("nas-metadata" + Path.DirectorySeparatorChar + source.ToString("N")))
            && !File.ReadAllText(documents.Single(path => path.EndsWith("catalog.json"))).Contains("fixture-secret", StringComparison.Ordinal));
    }
    internal static async Task FailureAsync()
    {
        string directory = FixtureDirectory(); var handler = new Handler { Count = 2 };
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, Guid.NewGuid());
        var cache = new NasMetadataCache(directory); var first = await NasMetadataSync.RunAsync(api, cache, default);
        handler.FailCollection = true; handler.Count = 3;
        try { await NasMetadataSync.RunAsync(api, cache, default); throw new Exception("Failed metadata accepted."); }
        catch (MusicApiException) { }
        Require((await cache.LoadAsync(api.SourceInstanceId, default))!.UpdatedAt == first.Snapshot.UpdatedAt);
        handler.FailCollection = false; handler.Count = 2; handler.FailAssets = true;
        var partial = await NasMetadataSync.RunAsync(api, cache, default);
        Require(partial.FailedAssets == 3 && (await cache.ReadLyricsAsync(first.Snapshot.Tracks[0].Reference, default))!.Lines[0].Text == "cached"
            && (await cache.ReadCoverAsync(api.SourceInstanceId, "../unsafe/cover", NasCoverSize.Large, default))!.Length == 4);
        handler.FailAssets = false; handler.Count = 0;
        await NasMetadataSync.RunAsync(api, cache, default);
        var empty = await new NasMetadataCache(directory).LoadAsync(api.SourceInstanceId, default);
        Require(empty!.Tracks.Count == 0 && empty.Albums.Count == 0 && empty.Artists.Count == 0 && empty.Playlists.Count == 0 && empty.Favorites.Count == 0);
    }
    internal static async Task CancelAsync()
    {
        var cache = new NasMetadataCache(FixtureDirectory()); var handler = new Handler { Count = 1 };
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, Guid.NewGuid());
        var first = await NasMetadataSync.RunAsync(api, cache, default);
        using var work = new CancellationTokenSource(); handler.CancelOnCollection = work; handler.Count = 2;
        try { await NasMetadataSync.RunAsync(api, cache, work.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Require((await cache.LoadAsync(api.SourceInstanceId, default))!.UpdatedAt == first.Snapshot.UpdatedAt);
        handler.CancelOnCollection = null; handler.Unauthorized = true;
        try { await NasMetadataSync.RunAsync(api, cache, default); throw new Exception("Authentication ignored."); }
        catch (MusicApiException ex) { Require(ex.Failure == MusicFailure.Unauthorized); }
        Require((await cache.LoadAsync(api.SourceInstanceId, default))!.UpdatedAt == first.Snapshot.UpdatedAt);
        int before = handler.Calls;
        try { await NasMetadataSync.RunAsync(api, cache, work.Token); throw new Exception("Pre-cancellation ignored."); }
        catch (OperationCanceledException) { Require(handler.Calls == before); }
    }
    internal static async Task CorruptionAsync()
    {
        string directory = FixtureDirectory(); var cache = new NasMetadataCache(directory); var handler = new Handler { Count = 1 };
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, Guid.NewGuid());
        var first = await NasMetadataSync.RunAsync(api, cache, default);
        foreach (var snapshot in new[] { first.Snapshot with { Tracks = [first.Snapshot.Tracks[0] with { SourceInstanceId = Guid.NewGuid() }] },
            first.Snapshot with { Tracks = [first.Snapshot.Tracks[0], first.Snapshot.Tracks[0]] },
            first.Snapshot with { Tracks = [first.Snapshot.Tracks[0] with { DurationSeconds = double.NaN }] } })
        {
            try { await cache.SaveAsync(snapshot, default); throw new Exception("Invalid metadata accepted."); }
            catch (InvalidDataException) { }
        }
        Require((await cache.LoadAsync(api.SourceInstanceId, default))!.UpdatedAt == first.Snapshot.UpdatedAt);
        string path = Directory.GetFiles(directory, "catalog.json", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(path, "{broken"); Require(await cache.LoadAsync(api.SourceInstanceId, default) is null);
        await cache.SaveAsync(first.Snapshot, default);
        var bytes = await File.ReadAllBytesAsync(path);
        var other = Guid.NewGuid(); string copied = Path.Combine(directory, "nas-metadata", other.ToString("N"), "catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(copied)!); await File.WriteAllBytesAsync(copied, bytes);
        Require(await cache.LoadAsync(other, default) is null);
        using (var file = File.Open(path, FileMode.Create)) file.SetLength(64L * 1024 * 1024 + 1);
        Require(await cache.LoadAsync(api.SourceInstanceId, default) is null);
        await cache.SaveAsync(first.Snapshot, default);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await cache.SaveAsync(first.Snapshot with { Tracks = [] }, cancelled.Token); throw new Exception("Cancelled commit accepted."); }
        catch (OperationCanceledException) { }
        Require((await cache.LoadAsync(api.SourceInstanceId, default))!.Tracks.Count == 1 && Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories).Length == 0);
    }
}
