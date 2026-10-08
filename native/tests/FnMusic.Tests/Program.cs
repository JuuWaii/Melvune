using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;
using FnMusic.Windows;

int passed = 0;
void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
async Task Test(string name, Func<Task> run) { await run(); passed++; Console.WriteLine("PASS " + name); }
async Task Fails(MusicFailure failure, Func<Task> run)
{
    try { await run(); throw new Exception("Expected API failure"); }
    catch (MusicApiException ex) { Check(ex.Failure == failure); }
}
var endpoint = ServerEndpoint.Parse("https://nas.example.invalid/music/");
await Test("persisted local index restores offline and playable references and incrementally reconciles changed added deleted files with bounded invalid document handling", LocalIndexVerification.RunAsync);
await Test("ten thousand offline cached rows and empty snapshots persist with atomic concurrent snapshot publication", LocalIndexVerification.CapacityAsync);
if (args.Contains("--verify-local-index")) { Console.WriteLine($"Local index checks: {passed} passed."); return; }
async Task VerifyRangeCacheAsync()
{
    await Test("strong Range cache retains hot blocks and evicts the least recently used within its fixed bound", RangeCacheVerification.StrongCacheAsync);
    await Test("canceled and changed-version Range blocks preserve validated cache and destination", RangeCacheVerification.FailurePreservesCacheAsync);
    await Test("weak or unavailable validators retain the previous single-block policy", RangeCacheVerification.UnversionedCacheAsync);
}
if (args.Contains("--verify-range-cache")) { await VerifyRangeCacheAsync(); Console.WriteLine($"Targeted checks: {passed} passed."); return; }
if (args.Length == 2 && args[0] == "--verify-embedded-producer")
{ await Test("independent FFmpeg tags and unchanged valid audio files", () => EmbeddedLyricsVerification.ProducerAsync(args[1])); return; }
async Task VerifyEmbeddedLyricsAsync()
{
    await Test("embedded lyric containers cover ID3 FLAC MP4 Ogg Opus APE WAV AIFF and ASF", EmbeddedLyricsVerification.ContainersAsync);
    await Test("embedded ID3 handles text encodings synchronized entries extended headers and unsynchronisation", EmbeddedLyricsVerification.Id3Async);
    await Test("embedded lyrics reject truncated malformed oversized and foreign tag structures", EmbeddedLyricsVerification.InvalidAsync);
    await Test("embedded lyrics skip multi-gigabyte audio payloads and cancel bounded reads", EmbeddedLyricsVerification.SparseAsync);
    await Test("local lyric sidecar precedence falls back to embedded tags without changing the audio file", EmbeddedLyricsVerification.SidecarsAsync);
}
if (args.Contains("--verify-embedded-lyrics")) { await VerifyEmbeddedLyricsAsync(); Console.WriteLine($"Embedded lyric checks: {passed} passed."); return; }
await Test("complete metadata catalog includes final rows beyond six pages and empty boundaries", TrackCatalogVerification.CompleteAsync);
await Test("metadata catalog rejects repeated changed truncated and oversized pages", TrackCatalogVerification.InconsistentAsync);
await Test("metadata catalog cancellation does not return a partial snapshot or issue later requests", TrackCatalogVerification.CancelAsync);
if (args.Contains("--verify-catalog")) { Console.WriteLine($"Catalog checks: {passed} passed."); return; }
await Test("metadata snapshot and assets survive restart with source isolation and no credentials or audio requests", MetadataCacheVerification.PersistenceAsync);
await Test("metadata failures retain the catalog and assets while an empty permission catalog replaces old rows", MetadataCacheVerification.FailureAsync);
await Test("metadata cancellation and expired authentication prevent later catalog commits", MetadataCacheVerification.CancelAsync);
await Test("metadata rejects corrupt oversized mixed-source duplicate and cancelled documents", MetadataCacheVerification.CorruptionAsync);
if (args.Contains("--verify-metadata")) { Console.WriteLine($"Catalog and metadata checks: {passed} passed."); return; }
await VerifyEmbeddedLyricsAsync();
if (args.Contains("--verify-cover-requests"))
{
    await Test("cover partial body timeout closes TCP input and permits an explicit retry", CoverRequestVerification.BodyTimeoutAsync);
    await Test("cover request deadline includes delayed headers and response body", CoverRequestVerification.TotalDeadlineAsync);
    await Test("cover caller cancellation preserves replacement session without retry", CoverRequestVerification.CallerCancellationAsync);
    await Test("cover disposal closes stalled body and rejects subsequent requests", CoverRequestVerification.DisposalAsync);
    await Test("cover truncated TCP body reports unavailable and preserves session for retry", CoverRequestVerification.DisconnectedBodyAsync);
    Console.WriteLine($"Cover checks: {passed} passed."); return;
}
await Test("JSON partial body timeout closes TCP input and preserves session for explicit retry", JsonRequestVerification.BodyTimeoutAsync);
await Test("JSON request deadline includes delayed headers and response body", JsonRequestVerification.TotalDeadlineAsync);
await Test("JSON caller cancellation preserves the replacement session without retry", JsonRequestVerification.CallerCancellationAsync);
await Test("JSON client disposal promptly closes stalled response body", JsonRequestVerification.DisposalAsync);
if (args.Contains("--verify-json-requests")) { Console.WriteLine($"JSON checks: {passed} passed."); return; }
await Test("cover partial body timeout closes TCP input and permits an explicit retry", CoverRequestVerification.BodyTimeoutAsync);
await Test("cover request deadline includes delayed headers and response body", CoverRequestVerification.TotalDeadlineAsync);
await Test("cover caller cancellation preserves replacement session without retry", CoverRequestVerification.CallerCancellationAsync);
await Test("cover disposal closes stalled body and rejects subsequent requests", CoverRequestVerification.DisposalAsync);
await Test("cover truncated TCP body reports unavailable and preserves session for retry", CoverRequestVerification.DisconnectedBodyAsync);
#if DEBUG
await Test("indexed file diagnostics validate paths and metadata identity without mutating tracks", async () =>
{
    string audio = "{\"path\":\"/fixture/song.flac\"}"; bool identityMatches = true; int calls = 0;
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        calls++; Check(request.Method == HttpMethod.Get);
        string data = request.RequestUri!.AbsolutePath.EndsWith("track/list")
            ? "{\"list\":[{\"guid\":\"song\",\"title\":\"fixture\",\"audioSpec\":" + audio + "}],\"total\":1}"
            : "{\"track\":{\"guid\":\"" + (identityMatches ? "song" : "wrong") + "\"},\"audioSpec\":{\"path\":\"/fixture/song.flac\"}}";
        return Json("{\"code\":0,\"data\":" + data + "}");
    }), Guid.NewGuid());
    var first = (await api.ListTrackFilesForVerificationAsync(1, 10, default)).Single();
    Check(first.Path == "/fixture/song.flac" && first.Track.SourceInstanceId == api.SourceInstanceId && calls == 1);
    audio = "null";
    Check((await api.ListTrackFilesForVerificationAsync(1, 10, default)).Single().Track.Reference == first.Track.Reference && calls == 3);
    identityMatches = false;
    await Fails(MusicFailure.InvalidResponse, () => api.ListTrackFilesForVerificationAsync(1, 10, default));
    foreach (string invalid in new[] { "false", "{\"path\":1}", "{\"path\":\"\"}", "{\"path\":\"" + new string('a', 4097) + "\"}" })
    { audio = invalid; await Fails(MusicFailure.InvalidResponse, () => api.ListTrackFilesForVerificationAsync(1, 10, default)); }
});
#endif
await Test("NAS library reconnect rechecks matching and requires current explicit confirmation", async () =>
{
    Guid source = Guid.NewGuid(); string directory = "/music/original";
    bool match = true, confirmation = false, admin = true, malformed = false; int reconnects = 0;
    using var api = new NasApiClient(endpoint, new AsyncFakeHandler(async request =>
    {
        string route = request.RequestUri!.AbsolutePath; object data;
        if (route.EndsWith("user/me")) data = new { role = admin ? "admin" : "member" };
        else if (route.EndsWith("shared-library/detail")) data = new { guid = "library", name = "fixture", path = directory, autoDownloadLyric = true, metadataPreference = "local_only" };
        else
        {
            Check(request.Method == HttpMethod.Post);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("guid").GetString() == "library" && body.RootElement.EnumerateObject().Count() == 2);
            if (route.EndsWith("reconnect/check")) data = malformed ? new { invalid = true } : (object)new { matchAccepted = match, requiresConfirmation = confirmation };
            else
            {
                Check(route.EndsWith("shared-library/reconnect")); reconnects++;
                directory = body.RootElement.GetProperty("path").GetString()!;
                data = new { reconnected = true, scanStarted = true };
            }
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = 0, data })) };
    }), source);
    var library = await api.GetSharedLibraryAsync(new(source, "library", "fixture"), default);
    var preflight = await api.CheckLibraryReconnectAsync(library, "/music/moved", default);
    Check(!preflight.NeedsConfirmation && reconnects == 0);
    match = false; confirmation = true;
    try { await api.ReconnectLibraryAsync(preflight, true, default); throw new Exception("Changed preflight accepted"); }
    catch (RemoteSettingsConflictException) { }
    Check(reconnects == 0);
    preflight = await api.CheckLibraryReconnectAsync(library, "/music/moved", default);
    await Fails(MusicFailure.Rejected, () => api.ReconnectLibraryAsync(preflight, false, default));
    Check(reconnects == 0);
    var result = await api.ReconnectLibraryAsync(preflight, true, default);
    Check(result.ScanStarted && result.Library.Path == "/music/moved" && result.Library.Library.Id == "library" && result.Library.AutoDownloadLyric && result.Library.MetadataPreference == "local_only" && reconnects == 1);
    try { await api.ReconnectLibraryAsync(preflight, true, default); throw new Exception("Stale library accepted"); }
    catch (RemoteSettingsConflictException) { }
    await Fails(MusicFailure.Rejected, () => api.CheckLibraryReconnectAsync(result.Library with { Library = result.Library.Library with { SourceInstanceId = Guid.NewGuid() } }, "/music/other", default));
    admin = false;
    await Fails(MusicFailure.Rejected, () => api.CheckLibraryReconnectAsync(result.Library, "/music/other", default));
    admin = true; malformed = true;
    await Fails(MusicFailure.InvalidResponse, () => api.CheckLibraryReconnectAsync(result.Library, "/music/other", default));
    Check(reconnects == 1);
});
await Test("NAS tasks validate states, source identity and guarded background actions", async () =>
{
    Guid source = Guid.NewGuid(); bool admin = true, done = false, cancelling = false, exists = true;
    long created = 1; int writes = 0; string? malformed = null;
    using var api = new NasApiClient(endpoint, new AsyncFakeHandler(async request =>
    {
        string path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post)
        {
            writes++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.EnumerateObject().Count() == 1 && body.RootElement.GetProperty("taskId").GetString() == "scan-1");
            if (path.EndsWith("task/cancel")) cancelling = true;
            else if (path.EndsWith("task/retry")) { done = false; cancelling = false; }
            else if (path.EndsWith("task/delete")) exists = false;
            else throw new Exception("Unexpected task write");
        }
        if (path.EndsWith("task/list") && malformed is not null)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":" + malformed + "}") };
        object data = path.EndsWith("user/me") ? new { role = admin ? "admin" : "member" } :
            new { list = exists ? new[] { new { id = "scan-1", type = "fileScan", name = "fixture", total = 10, successCount = 3,
                failCount = 1, canceledCount = 0, done, cancelling, canceled = false, retryable = true, createdAt = created } } : [] };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = 0, data })) };
    }), source);
    var task = (await api.ListBackgroundTasksAsync(default)).Single();
    Check(task.CanCancel && !task.CanDelete && !task.CanRetry && task.FailCount == 1);
    await Fails(MusicFailure.Rejected, () => api.ChangeBackgroundTaskAsync(task with { SourceInstanceId = Guid.NewGuid() }, NasTaskAction.Cancel, default));
    Check(writes == 0);
    created = 2;
    try { await api.ChangeBackgroundTaskAsync(task, NasTaskAction.Cancel, default); throw new Exception("Reused task identity accepted"); }
    catch (RemoteSettingsConflictException) { }
    Check(writes == 0); created = 1;
    var canceled = (await api.ChangeBackgroundTaskAsync(task, NasTaskAction.Cancel, default)).Single();
    Check(canceled.Cancelling && !canceled.CanCancel && !canceled.CanDelete && writes == 1);
    try { await api.ChangeBackgroundTaskAsync(task, NasTaskAction.Cancel, default); throw new Exception("Duplicate cancel accepted"); }
    catch (RemoteSettingsConflictException) { }
    done = true; cancelling = false;
    task = (await api.ListBackgroundTasksAsync(default)).Single();
    Check(task.CanRetry && task.CanDelete);
    admin = false;
    await Fails(MusicFailure.Rejected, () => api.ChangeBackgroundTaskAsync(task, NasTaskAction.Delete, default));
    Check(writes == 1); admin = true;
    Check(!(await api.ChangeBackgroundTaskAsync(task, NasTaskAction.Retry, default)).Single().Done && writes == 2);
    try { await api.ChangeBackgroundTaskAsync(task, NasTaskAction.Delete, default); throw new Exception("Running task deleted"); }
    catch (RemoteSettingsConflictException) { }
    done = true;
    Check((await api.ChangeBackgroundTaskAsync(task, NasTaskAction.Delete, default)).Count == 0 && writes == 3);
    foreach (string invalid in new[] { "{}", "[null]", "[{\"id\":\"x\"}]", "{\"list\":false}" })
    {
        malformed = invalid;
        await Fails(MusicFailure.InvalidResponse, () => api.ListBackgroundTasksAsync(default));
    }
});
await Test("NAS task library association is optional, exact and guarded before actions", async () =>
{
    string extension = "{\"libraryGUID\":\"library-A\"}"; int writes = 0;
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        if (request.Method == HttpMethod.Post) writes++;
        string data = request.RequestUri!.AbsolutePath.EndsWith("user/me") ? "{\"role\":\"admin\"}" :
            "[{\"id\":\"scan\",\"type\":\"fileScan\",\"name\":\"same display name\",\"total\":3,\"successCount\":1,\"failCount\":0,\"canceledCount\":0,\"done\":false,\"createdAt\":1,\"ext\":" + extension + "}]";
        return Json("{\"code\":0,\"data\":" + data + "}");
    }), Guid.NewGuid());
    var original = (await api.ListBackgroundTasksAsync(default)).Single();
    Check(original.LibraryId == "library-A");
    extension = "{\"libraryGUID\":\"library-a\"}";
    try { await api.ChangeBackgroundTaskAsync(original, NasTaskAction.Cancel, default); throw new Exception("Changed library association accepted"); }
    catch (RemoteSettingsConflictException) { }
    Check(writes == 0);
    foreach (string absent in new[] { "null", "{}", "{\"libraryGUID\":null}", "{\"libraryGUID\":\"\"}" })
    { extension = absent; Check((await api.ListBackgroundTasksAsync(default)).Single().LibraryId is null); }
    foreach (string invalid in new[] { "false", "[]", "{\"libraryGUID\":1}", "{\"libraryGUID\":\"" + new string('a', 257) + "\"}" })
    { extension = invalid; await Fails(MusicFailure.InvalidResponse, () => api.ListBackgroundTasksAsync(default)); }
    Check(writes == 0);
});
await Test("theme settings persist all modes and reject corrupt or unknown preferences", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "fnmusic-theme-" + Guid.NewGuid().ToString("N"));
    var store = new ThemeSettingsStore(directory);
    Check(store.Load() == ThemePreference.System);
    try
    {
        foreach (var preference in Enum.GetValues<ThemePreference>())
        {
            store.Save(preference);
            Check(new ThemeSettingsStore(directory).Load() == preference);
        }
        try { store.Save((ThemePreference)99); throw new Exception("Invalid preference saved"); }
        catch (ArgumentOutOfRangeException) { }
        Check(store.Load() == ThemePreference.Dark);
        foreach (var invalid in new[] { "null", "{}", "{\"Theme\":\"invalid\"}", new string(' ', 4097) })
        {
            File.WriteAllText(Path.Combine(directory, "appearance.json"), invalid);
            try { store.Load(); throw new Exception("Corrupt preference accepted"); }
            catch (InvalidDataException) { }
        }
        store.Save(ThemePreference.System);
        Check(store.Load() == ThemePreference.System);
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
    return Task.CompletedTask;
});
await Test("local playlists retain cross-source references, ordering and reject stale writes", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "fnmusic-playlists-" + Guid.NewGuid().ToString("N"));
    var store = new LocalPlaylistStore(directory);
    try
    {
        var empty = await store.LoadAsync();
        Check(empty.Revision == 0 && empty.Playlists.Count == 0);
        var first = LocalPlaylistEntry.FromTrack(new MusicTrack("same-id", "第一首", "Artist", 20, false) { SourceInstanceId = Guid.NewGuid(), CoverId = "cover-a" }, "library-a");
        var second = LocalPlaylistEntry.FromTrack(new MusicTrack("same-id", "第二首", "Artist", 30, false) { SourceInstanceId = Guid.NewGuid() }, "library-b");
        var playlist = new LocalPlaylist(Guid.NewGuid(), "跨库歌单", [first, second]);
        var saved = await store.SaveAsync(empty with { Playlists = [playlist] });
        var reloaded = await new LocalPlaylistStore(directory).LoadAsync();
        Check(reloaded.Revision == 1 && reloaded.Playlists[0].Entries.SequenceEqual(playlist.Entries));
        Check(reloaded.Playlists[0].Entries[0].ToTrack().CoverId == "cover-a");
        Check(reloaded.Playlists[0].Entries[1].ToTrack().Reference == second.Track);
        var reordered = saved with { Playlists = [playlist with { Entries = [second, first] }] };
        var updated = await store.SaveAsync(reordered);
        try { await store.SaveAsync(saved); throw new Exception("Stale write accepted"); }
        catch (PlaylistConflictException) { }
        Check((await store.LoadAsync()).Playlists[0].Entries[0].Id == second.Id);
        // No provider is connected: references and snapshot metadata must remain intact.
        Check((await store.LoadAsync()).Playlists[0].Entries.Count == 2);
        var stale = updated;
        var concurrent = new[] { new LocalPlaylistStore(directory), new LocalPlaylistStore(directory) };
        int successes = 0, conflicts = 0;
        await Task.WhenAll(concurrent.Select(async writer =>
        {
            try { await writer.SaveAsync(stale); Interlocked.Increment(ref successes); }
            catch (PlaylistConflictException) { Interlocked.Increment(ref conflicts); }
        }));
        Check(successes == 1 && conflicts == 1);
        var beforeInvalid = await store.LoadAsync();
        try
        {
            await store.SaveAsync(beforeInvalid with { Playlists = [playlist with { Entries = [first, first] }] });
            throw new Exception("Duplicate reference accepted");
        }
        catch (InvalidDataException) { }
        Check((await store.LoadAsync()).Revision == beforeInvalid.Revision);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await store.SaveAsync(beforeInvalid, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0);
        File.WriteAllText(Path.Combine(directory, "local-playlists.json"), "{\"Version\":99}");
        try { await store.SaveAsync(beforeInvalid); throw new Exception("Corrupt store overwritten"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(Path.Combine(directory, "local-playlists.json")).Contains("99"));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
});
await Test("library membership survives snapshots without changing source-scoped track identity", () =>
{
    string[] memberships = ["library-a", "library-b"];
    var first = new MusicTrack("same", "Title", "Artist", 10, false) { SourceInstanceId = Guid.NewGuid(), LibraryIds = memberships };
    var entry = LocalPlaylistEntry.FromTrack(first);
    memberships[0] = "changed-after-snapshot";
    Check(entry.GetLibraryIds().SequenceEqual(new[] { "library-a", "library-b" }));
    var restored = entry.ToTrack();
    Check(restored.Reference == first.Reference && restored.LibraryIds.Count == 2);
    Check((restored with { LibraryIds = ["another-library"] }).Reference == first.Reference);
    var otherSource = restored with { SourceInstanceId = Guid.NewGuid() };
    Check(restored.GetLibraries()[0] != otherSource.GetLibraries()[0] && restored.Reference != otherSource.Reference);
    var sameSnapshot = entry with { LibraryIds = new[] { "library-a", "library-b" } };
    Check(entry == sameSnapshot && entry.GetHashCode() == sameSnapshot.GetHashCode());
    Check(entry != (entry with { LibraryIds = ["library-a"] }));
    return Task.CompletedTask;
});
await Test("playlist membership reads version one and persists version two without losing references", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "fnmusic-playlist-membership-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string file = Path.Combine(directory, "local-playlists.json");
    var legacy = new LocalPlaylistEntry(Guid.NewGuid(), new(Guid.NewGuid(), "legacy-song"), "Title", "Artist", "Album", 10, false, "legacy-library");
    var unknown = legacy with { Id = Guid.NewGuid(), Track = new(Guid.NewGuid(), "unknown-song"), LibraryId = null };
    var playlistId = Guid.NewGuid();
    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { Version = 1, Revision = 3, Playlists = new[] {
        new { Id = playlistId, Name = "旧歌单", Entries = new[] { legacy, unknown }.Select(entry => new {
            entry.Id, entry.Track, entry.Title, entry.Artist, entry.Album, entry.DurationSeconds, entry.IsCue, entry.LibraryId, entry.CoverId }) }
    } }));
    var store = new LocalPlaylistStore(directory);
    var loaded = await store.LoadAsync();
    Check(loaded.Revision == 3 && loaded.Playlists[0].Entries[0].ToTrack().LibraryIds.SequenceEqual(new[] { "legacy-library" }));
    Check(loaded.Playlists[0].Entries[1].ToTrack().LibraryIds.Count == 0);
    var multi = LocalPlaylistEntry.FromTrack(new MusicTrack("multi", "多个库", "Artist", 10, false) {
        SourceInstanceId = Guid.NewGuid(), LibraryIds = ["library-a", "library-b"] });
    await store.SaveAsync(loaded with { Playlists = [loaded.Playlists[0] with { Entries = [legacy, unknown, multi] }] });
    using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(file))) Check(json.RootElement.GetProperty("Version").GetInt32() == 2);
    var restored = await new LocalPlaylistStore(directory).LoadAsync();
    Check(restored.Revision == 4 && restored.Playlists[0].Id == playlistId && restored.Playlists[0].Entries[0] == legacy);
    Check(restored.Playlists[0].Entries[1].Track == unknown.Track && restored.Playlists[0].Entries[2] == multi);
    byte[] before = await File.ReadAllBytesAsync(file);
    foreach (var invalid in new[] {
        multi with { LibraryIds = ["library-a", "library-a"] },
        multi with { LibraryIds = [""] },
        multi with { LibraryIds = Enumerable.Range(0, 101).Select(i => "library-" + i).ToArray(), LibraryId = "library-0" },
        multi with { LibraryId = "wrong-primary" }
    })
    {
        try { await store.SaveAsync(restored with { Playlists = [new(playlistId, "Invalid", [invalid])] }); throw new Exception("Invalid memberships accepted"); }
        catch (InvalidDataException) { }
        byte[] afterInvalid = await File.ReadAllBytesAsync(file);
        Check(before.SequenceEqual(afterInvalid));
    }
    File.Delete(file); File.Delete(Path.Combine(directory, "local-playlists.json.lock")); Directory.Delete(directory);
});
await Test("local library catalog migrates identities and preserves libraries across conflicts and corruption", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "fnmusic-libraries-" + Guid.NewGuid().ToString("N"));
    var store = new LocalLibraryStore(directory);
    try
    {
        var legacy = new LocalMusicSettings(Guid.NewGuid(), Path.Combine(directory, "old-music"));
        await new LocalMusicSettingsStore(directory).SaveAsync(legacy, default);
        var migrated = await store.LoadAsync();
        Check(migrated.Revision == 0 && migrated.Libraries.Single().SourceId == legacy.SourceId);
        var added = new LocalLibrary(Guid.NewGuid(), Path.Combine(directory, "second"), "第二库", "收藏");
        var saved = await store.SaveAsync(migrated with { Libraries = migrated.Libraries.Append(added).ToArray() });
        var restored = await new LocalLibraryStore(directory).LoadAsync();
        Check(restored.Libraries.SequenceEqual(saved.Libraries) && restored.Libraries[0].SourceId == legacy.SourceId);
        var updated = await store.SaveAsync(restored with { Libraries = [restored.Libraries[0] with { Name = "改名", Group = "无损" }, added] });
        Check(updated.Libraries[0].Settings == legacy);
        try { await store.SaveAsync(restored); throw new Exception("Stale catalog accepted"); } catch (LocalLibraryConflictException) { }
        try { await store.SaveAsync(updated with { Libraries = [added, added with { SourceId = Guid.NewGuid(), Directory = added.Directory + Path.DirectorySeparatorChar }] }); throw new Exception("Duplicate path accepted"); } catch (InvalidDataException) { }
        Check((await store.LoadAsync()).Revision == updated.Revision);
        var removed = await store.SaveAsync(updated with { Libraries = [] });
        Check((await store.LoadAsync()).Libraries.Count == 0); // Legacy file must not resurrect removed libraries.
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await store.SaveAsync(removed, cancelled.Token); throw new Exception("Cancel ignored"); } catch (OperationCanceledException) { }
        File.WriteAllText(Path.Combine(directory, "local-libraries.json"), "{\"Version\":99}");
        try { await store.SaveAsync(removed); throw new Exception("Corrupt catalog overwritten"); } catch (InvalidDataException) { }
        Check(File.ReadAllText(Path.Combine(directory, "local-libraries.json")).Contains("99"));
        Check(File.Exists(Path.Combine(directory, "local-music.json")) && Directory.GetFiles(directory, "*.tmp").Length == 0);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
});

await Test("PCM slow input publishes partial data and isolates cancelled seeks", async () =>
{
    using var input = new PausingPcmStream();
    await using var cache = new PcmReadAheadBuffer(input);
    await input.Paused.Task.WaitAsync(TimeSpan.FromSeconds(3));
    byte[] sample = new byte[4];
    Check(await cache.ReadAtAsync(0, sample, default).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 4);
    Check(sample.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
    using var cancelled = new CancellationTokenSource();
    var pending = cache.ReadAtAsync(100, sample, cancelled.Token).AsTask();
    cancelled.Cancel();
    try { await pending; throw new Exception("Seek cancellation ignored"); } catch (OperationCanceledException) { }
    Check(await cache.ReadAtAsync(0, sample, default) == 4);
    var waiting = cache.ReadAtAsync(100, sample, default).AsTask();
    await cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    try { await waiting; throw new Exception("Disposed reader not released"); } catch (ObjectDisposedException) { }
    await cache.DisposeAsync();
    Check(cache.BufferedBytes == 0 && input.CanRead);
});
await Test("PCM read-ahead supports backward seeks and bounded memory", async () =>
{
    byte[] bytes = Enumerable.Range(0, 180000).Select(i => (byte)(i % 251)).ToArray();
    using var input = new MemoryStream(bytes);
    await using var cache = new PcmReadAheadBuffer(input, 150000);
    byte[] output = new byte[128];
    Check(await cache.ReadAtAsync(140000, output, default) == 128 && output.SequenceEqual(bytes.Skip(140000).Take(128)));
    Check(await cache.ReadAtAsync(10, output, default) == 128 && output.SequenceEqual(bytes.Skip(10).Take(128)));
    try { await cache.ReadAtAsync(150000, output, default); throw new Exception("Limit ignored"); } catch (PcmCacheLimitException) { }
    Check(cache.BufferedBytes == 150000);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { await cache.ReadAtAsync(0, output, cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
});
await Test("lyrics keep CR, LF and CRLF rows separate for timed and plain text", () =>
{
    foreach (string newline in new[] { "\r", "\n", "\r\n" })
    {
        var timed = Lyrics.Parse(string.Join(newline, "[ar:fixture]", "[offset:150]", "[00:01.05]第一句", "[00:03.125]第二句"), -50);
        Check(timed.IsTimed && timed.Lines.Count == 2 && timed.Lines[0].Text == "第一句" && timed.Lines[1].Text == "第二句");
        Check(Math.Abs(timed.Lines[0].Seconds - .85) < 1e-9 && Math.Abs(timed.Lines[1].Seconds - 2.925) < 1e-9);
        var plain = Lyrics.Parse(string.Join(newline, "[ti:fixture]", "第一句", "第二句"));
        Check(!plain.IsTimed && plain.Lines.Select(line => line.Text).SequenceEqual(new[] { "第一句", "第二句" }));
    }
    var mixed = Lyrics.Parse("[00:01]第一句\r\n[00:02]第二句\r[00:03]第三句\n[00:04]第四句");
    Check(mixed.Lines.Select(line => line.Text).SequenceEqual(new[] { "第一句", "第二句", "第三句", "第四句" }));
    return Task.CompletedTask;
});
await Test("lyrics timestamps translations offsets and seeking", () =>
{
    var lyrics = Lyrics.Parse("[ar:Artist]\n[offset:100]\n[00:20.25]later\n[00:01.00][00:03.000]first\n[00:01.00]translation", 200);
    Check(lyrics.IsTimed && lyrics.Lines.Count == 3 && Math.Abs(lyrics.Lines[0].Seconds - 1.1) < .001);
    Check(lyrics.Lines[0].Text == "first\ntranslation" && lyrics.FindLine(0) == -1 && lyrics.FindLine(4) == 1 && lyrics.FindLine(100) == 2);
    Check(lyrics.FindLine(double.NaN) == -1 && !Lyrics.Parse("[ti:Title]\nplain text").IsTimed);
    Check(Lyrics.Parse("[ti:Title]").Lines.Count == 0);
    return Task.CompletedTask;
});
await Test("NAS lyrics select preferred and encode scoped track", async () =>
{
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        Check(request.RequestUri!.AbsolutePath.EndsWith("/lyric/list") && request.RequestUri.Query == "?trackGUID=a%26b");
        return Json("""{"code":0,"data":{"preferred":"manual","list":[{"guid":"embedded","source":1,"content":"[00:01]embedded"},{"guid":"manual","source":4,"content":"[00:02]preferred","offset":500}]}}""");
    }));
    var lyrics = await api.GetLyricsAsync(new(Guid.Empty, "a&b"), default);
    Check(lyrics.Lines.Single() == new LyricLine(2.5, "preferred"));
});
await Test("cover retrieval rejects redirects and non-images", async () =>
{
    using var redirect = new NasApiClient(endpoint, new FakeHandler(_ => new(HttpStatusCode.Found)));
    await Fails(MusicFailure.RedirectRejected, () => redirect.GetCoverAsync("cover", default));
    using var html = new NasApiClient(endpoint, new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("html") }));
    await Fails(MusicFailure.InvalidResponse, () => html.GetCoverAsync("cover", default));
    using var missing = new NasApiClient(endpoint, new FakeHandler(_ => new(HttpStatusCode.NotFound)));
    Check(await missing.GetCoverAsync("cover", default) is null);
    using var image = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        Check(request.RequestUri!.Query == "?coverId=a%26b&size=600");
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new("image/png");
        return new(HttpStatusCode.OK) { Content = content };
    }));
    Check((await image.GetCoverAsync("a&b", default))!.SequenceEqual(new byte[] { 1, 2, 3 }));
});
await Test("cover sizes follow server tiers and reject unbounded requests before transport", async () =>
{
    int requests = 0;
    using var image = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        requests++;
        Check(request.RequestUri!.Query == "?coverId=a%26b&size=1024");
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new("image/png");
        return new(HttpStatusCode.OK) { Content = content };
    }));
    Check((await image.GetCoverAsync("a&b", NasCoverSize.Large, default))!.SequenceEqual(new byte[] { 1, 2, 3 }));
    foreach (int invalidSize in new[] { 0, -1, 256, 4096, int.MaxValue })
    {
        try { await image.GetCoverAsync("a&b", (NasCoverSize)invalidSize, default); throw new Exception("Invalid size accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }
    Check(requests == 1);
});
await Test("large covers retain redirect, media type and body size protections", async () =>
{
    using var redirect = new NasApiClient(endpoint, new FakeHandler(_ => new(HttpStatusCode.Found)));
    await Fails(MusicFailure.RedirectRejected, () => redirect.GetCoverAsync("cover", NasCoverSize.Large, default));
    using var html = new NasApiClient(endpoint, new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("html") }));
    await Fails(MusicFailure.InvalidResponse, () => html.GetCoverAsync("cover", NasCoverSize.Large, default));
    using var oversized = new NasApiClient(endpoint, new FakeHandler(_ =>
    {
        var content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]);
        content.Headers.ContentType = new("image/png");
        return new(HttpStatusCode.OK) { Content = content };
    }));
    await Fails(MusicFailure.InvalidResponse, () => oversized.GetCoverAsync("cover", NasCoverSize.Large, default));
});
await Test("NAS settings preserve language, scope writes and detect remote changes", async () =>
{
    Guid source = Guid.NewGuid(); bool admin = true, autoLyric = false; string server = "original", metadata = "local_only", mode = "none";
    string[] accessIds = []; int writes = 0;
    object Library() => new { guid = "lib&a", name = "library", path = "/music/library", autoDownloadLyric = autoLyric, metadataPreference = metadata };
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        string path = request.RequestUri!.AbsolutePath; object? data;
        if (request.Method == HttpMethod.Post)
        {
            writes++;
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = body.RootElement;
            if (path.EndsWith("settings/server")) { Check(root.GetProperty("lang").GetString() == "zh-CN"); server = root.GetProperty("name").GetString()!; }
            if (path.EndsWith("shared-library/edit"))
            {
                Check(root.GetProperty("guid").GetString() == "lib&a" && root.GetProperty("path").GetString() == "/music/library");
                autoLyric = root.GetProperty("autoDownloadLyric").GetBoolean(); metadata = root.GetProperty("metadataPreference").GetString()!;
            }
            if (path.EndsWith("settings/user"))
            {
                var access = root.GetProperty("defaultSharedLibraryAccess"); mode = access.GetProperty("mode").GetString()!;
                accessIds = access.GetProperty("guids").EnumerateArray().Select(value => value.GetString()!).ToArray();
            }
            data = null;
        }
        else if (path.EndsWith("user/me")) data = new { role = admin ? "admin" : "member" };
        else if (path.EndsWith("settings/server")) data = new { name = server, lang = "zh-CN" };
        else if (path.EndsWith("settings/user")) data = new { defaultSharedLibraryAccess = new { mode, sharedLibraries = accessIds.Select(id => new { guid = id }) } };
        else if (path.EndsWith("shared-library/detail")) { Check(request.RequestUri.Query == "?guid=lib%26a"); data = Library(); }
        else data = new { list = new[] { Library() } };
        return Json(JsonSerializer.Serialize(new { code = 0, data }));
    }), source);
    var original = await api.GetServerSettingsAsync(default);
    var updated = await api.RenameServerAsync(original, "renamed", default); Check(updated.Name == "renamed" && updated.Language == "zh-CN");
    int beforeConflict = writes;
    try { await api.RenameServerAsync(original, "stale", default); throw new Exception("Stale settings accepted"); } catch (RemoteSettingsConflictException) { }
    Check(writes == beforeConflict);
    var library = (await api.ListSharedLibrariesAsync(default)).Single();
    var changed = await api.UpdateSharedLibraryAsync(library, true, "local_preferred", default);
    Check(changed.AutoDownloadLyric && changed.MetadataPreference == "local_preferred" && changed.Path == library.Path);
    try { await api.UpdateSharedLibraryAsync(library, false, "local_only", default); throw new Exception("Stale library accepted"); } catch (RemoteSettingsConflictException) { }
    var access = await api.GetDefaultLibraryAccessAsync(default);
    var partial = await api.UpdateDefaultLibraryAccessAsync(access, "partial", [library.Library], default);
    Check(partial.Mode == "partial" && partial.LibraryIds.Single() == library.Library.Id);
    var all = await api.UpdateDefaultLibraryAccessAsync(partial, "all", [library.Library], default);
    Check(all.Mode == "all" && accessIds.Length == 0);
    beforeConflict = writes;
    await Fails(MusicFailure.Rejected, () => api.UpdateSharedLibraryAsync(changed with { Library = library.Library with { SourceInstanceId = Guid.NewGuid() } }, false, "local_only", default));
    admin = false;
    await Fails(MusicFailure.Rejected, () => api.RenameServerAsync(updated, "forbidden", default));
    Check(writes == beforeConflict);
});

await Test("NAS playlist mutations verify readback and reject foreign sources and stale snapshots", async () =>
{
    Guid source = Guid.NewGuid(); string name = "initial"; long version = 1; bool exists = true, ignoreWrites = false;
    var members = new List<string> { "track-a" }; int writes = 0, calls = 0;
    object Summary() => new { guid = "p&a", name, coverId = (string?)null, updatedAt = version };
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        calls++; string route = request.RequestUri!.AbsolutePath.Split('/').Last();
        if (request.Method == HttpMethod.Post)
        {
            writes++;
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = body.RootElement;
            if (route == "create") { Check(root.GetProperty("coverId").ValueKind == JsonValueKind.Null); name = root.GetProperty("name").GetString()!; exists = true; return Json(JsonSerializer.Serialize(new { code = 0, data = Summary() })); }
            Check(root.GetProperty("guid").GetString() == "p&a");
            if (!ignoreWrites)
            {
                if (route == "edit") name = root.GetProperty("name").GetString()!;
                if (route == "delete") exists = false;
                if (route is "add-track" or "remove-track")
                    foreach (var id in root.GetProperty("trackGUIDs").EnumerateArray().Select(value => value.GetString()!))
                        if (route == "add-track") members.Add(id); else members.Remove(id);
                version++;
            }
            return Json("{\"code\":0,\"data\":null}");
        }
        if (route == "detail") { Check(request.RequestUri.Query == "?guid=p%26a"); return Json(JsonSerializer.Serialize(new { code = 0, data = Summary() })); }
        if (request.RequestUri.AbsolutePath.Contains("track/playlist-detail/"))
        {
            Check(request.RequestUri.Query == "?playlistGUID=p%26a&page=1&size=100&sort=trackAddedAt%2Casc");
            return Json(JsonSerializer.Serialize(new { code = 0, data = new { total = members.Count, list = members.Select(id => new { guid = id, title = id, duration = 1000 }) } }));
        }
        return Json(JsonSerializer.Serialize(new { code = 0, data = new { list = exists ? new[] { Summary() } : [] } }));
    }), source);
    var reference = new PlaylistReference(source, "p&a");
    var list = await api.ListPlaylistsAsync(default); Check(list.Single().Reference == reference);
    var snapshot = await api.ReadPlaylistSnapshotAsync(reference, default);
    Check(snapshot.Tracks.Single().SourceInstanceId == source);
    int previousCalls = calls;
    await Fails(MusicFailure.Rejected, () => api.GetPlaylistAsync(new(Guid.NewGuid(), "p&a"), default));
    await Fails(MusicFailure.Rejected, () => api.ChangePlaylistTracksAsync(snapshot, [new(Guid.NewGuid(), "track-b")], true, default));
    Check(calls == previousCalls);
    var renamed = await api.RenamePlaylistAsync(snapshot.Playlist, "改名", default); Check(renamed.Name == "改名");
    int beforeConflict = writes;
    try { await api.RenamePlaylistAsync(snapshot.Playlist, "stale", default); throw new Exception("Stale rename accepted"); } catch (RemotePlaylistConflictException) { }
    Check(writes == beforeConflict);
    snapshot = await api.ReadPlaylistSnapshotAsync(reference, default);
    var added = await api.ChangePlaylistTracksAsync(snapshot, [new(source, "track-b")], true, default); Check(added.Tracks.Count == 2);
    var removed = await api.ChangePlaylistTracksAsync(added, [new(source, "track-a")], false, default); Check(removed.Tracks.Single().Id == "track-b");
    members.Add("web-added"); // Same timestamp still detects changed membership.
    beforeConflict = writes;
    try { await api.DeletePlaylistAsync(removed, default); throw new Exception("Stale delete accepted"); } catch (RemotePlaylistConflictException) { }
    Check(writes == beforeConflict && exists);
    ignoreWrites = true;
    try { await api.RenamePlaylistAsync(removed.Playlist, "not-applied", default); throw new Exception("Missing readback accepted"); } catch (RemotePlaylistConflictException) { }
    ignoreWrites = false;
    await api.DeletePlaylistAsync(await api.ReadPlaylistSnapshotAsync(reference, default), default); Check(!exists);
    Check((await api.CreatePlaylistAsync("新歌单", default)).Name == "新歌单");
    beforeConflict = calls;
    try { await api.CreatePlaylistAsync(new string('x', 33), default); throw new Exception("Invalid name accepted"); } catch (ArgumentException) { }
    Check(calls == beforeConflict);
});
await Test("NAS playlist parsing rejects malformed shapes and incomplete pages", async () =>
{
    Guid source = Guid.NewGuid();
    foreach (string payload in new[] { "{\"guid\":\"p\",\"name\":\"n\",\"updatedAt\":\"wrong\"}", "{\"guid\":\"other\",\"name\":\"n\"}", "{\"guid\":\"p\",\"name\":\"n\",\"coverId\":42}" })
    {
        using var api = new NasApiClient(endpoint, new FakeHandler(_ => Json("{\"code\":0,\"data\":" + payload + "}")), source);
        await Fails(MusicFailure.InvalidResponse, () => api.GetPlaylistAsync(new(source, "p"), default));
    }
    using var incomplete = new NasApiClient(endpoint, new FakeHandler(request => Json(request.RequestUri!.AbsolutePath.Contains("playlist-detail")
        ? "{\"code\":0,\"data\":{\"list\":[],\"total\":101}}"
        : "{\"code\":0,\"data\":{\"guid\":\"p\",\"name\":\"n\"}}")), source);
    await Fails(MusicFailure.InvalidResponse, () => incomplete.ReadPlaylistSnapshotAsync(new(source, "p"), default));
});

await Test("favorite list maps state and mutations use explicit scoped bodies", async () =>
{
    int calls = 0;
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        calls++;
        if (request.Method == HttpMethod.Get)
        {
            Check(request.RequestUri!.AbsolutePath.EndsWith("/favorite-track/list") && request.RequestUri.Query == "?page=2&size=1");
            return Json("""{"code":0,"data":{"list":[{"guid":"a&b","title":"收藏曲目"}],"total":3}}""");
        }
        Check(request.Method == HttpMethod.Post);
        Check(request.RequestUri!.AbsolutePath.EndsWith(calls == 2 ? "/favorite-track/create" : "/favorite-track/delete"));
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        Check(body.RootElement.GetProperty("trackGUID").GetString() == "a&b" && body.RootElement.EnumerateObject().Count() == 1);
        return Json("""{"code":0,"data":null}""");
    }));
    var page = await api.ListFavoritesAsync(2, 1, default);
    Check(page.Total == 3 && page.Tracks.Single().IsFavorite);
    await api.SetFavoriteAsync("a&b", true, default);
    await api.SetFavoriteAsync("a&b", false, default);
    try { await api.SetFavoriteAsync(" ", true, default); throw new Exception("Invalid ID accepted"); } catch (ArgumentException) { }
    Check(calls == 3);
});
await Test("favorite failures are not reported as successful mutations", async () =>
{
    using var rejected = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":500}""")));
    await Fails(MusicFailure.Rejected, () => rejected.SetFavoriteAsync("a", true, default));
    using var unauthorized = new NasApiClient(endpoint, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
    await Fails(MusicFailure.Unauthorized, () => unauthorized.SetFavoriteAsync("a", false, default));
    using var invalid = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"list":[{"guid":"a"}],"total":0}}""")));
    await Fails(MusicFailure.InvalidResponse, () => invalid.ListFavoritesAsync(1, 1, default));
    using var tracks = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"list":[{"guid":"a","isFavorite":true},{"guid":"b","isFavorite":false}],"total":2}}""")));
    var page = await tracks.ListTracksAsync(1, 50, default);
    Check(page.Tracks[0].IsFavorite && !page.Tracks[1].IsFavorite);
});
await Test("artist albums use scoped encoded pagination and strict collection parsing", async () =>
{
    int calls = 0;
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        calls++;
        Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/album/artist-detail/list"));
        Check(request.RequestUri!.Query == "?artistGUID=a%26b&page=2&size=1");
        return Json("""{"code":0,"data":{"list":[{"guid":"album-a","name":"测试专辑","trackCount":2}],"total":3}}""");
    }));
    var result = await api.ListArtistAlbumsAsync("a&b", 2, 1, default);
    Check(result.Total == 3 && result.Items.Single().Id == "album-a");
    try { await api.ListArtistAlbumsAsync(" ", 2, 1, default); throw new Exception("Invalid ID accepted"); } catch (ArgumentException) { }
    try { await api.ListArtistAlbumsAsync("a", 0, 1, default); throw new Exception("Invalid page accepted"); } catch (ArgumentException) { }
    Check(calls == 1);
    using var empty = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"list":[],"total":0}}""")));
    Check((await empty.ListArtistAlbumsAsync("a", 1, 50, default)).Items.Count == 0);
    using var malformed = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"list":[{"guid":"a","name":"A"}],"total":0}}""")));
    await Fails(MusicFailure.InvalidResponse, () => malformed.ListArtistAlbumsAsync("a", 1, 50, default));
});
await Test("late unauthorized responses cannot invalidate a replaced session", async () =>
{
    foreach (bool businessError in new[] { false, true })
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var api = new NasApiClient(endpoint, new AsyncFakeHandler(request =>
        {
            if (++calls == 1) return response.Task;
            Check(request.Headers.GetValues("Cookie").Single() == "music-token=new-session");
            return Task.FromResult(Json("""{"code":0,"data":{"name":"测试账户"}}"""));
        }));
        api.RestoreSession("old-session");
        var pending = api.GetCurrentUserAsync(default);
        api.RestoreSession("new-session");
        response.SetResult(businessError ? Json("""{"code":120001}""") : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Fails(MusicFailure.Unavailable, () => pending);
        Check((await api.GetCurrentUserAsync(default)).Name == "测试账户");
    }
});
await Test("network failure preserves authentication for collection retry", async () =>
{
    int calls = 0;
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        Check(request.Headers.GetValues("Cookie").Single() == "music-token=test-session");
        if (++calls == 1) throw new HttpRequestException("synthetic failure");
        return Json("""{"code":0,"data":{"list":[],"total":0}}""");
    }));
    api.RestoreSession("test-session");
    await Fails(MusicFailure.Unavailable, () => api.ListCollectionsAsync(CollectionKind.Album, 1, 50, default));
    Check((await api.ListCollectionsAsync(CollectionKind.Album, 1, 50, default)).Total == 0 && calls == 2);
});
await Test("collection lists preserve totals and optional counts", async () =>
{
    foreach (var kind in new[] { CollectionKind.Album, CollectionKind.Artist })
    {
        using var api = new NasApiClient(endpoint, new FakeHandler(request =>
        {
            Check(request.Method == HttpMethod.Get);
            Check(request.RequestUri!.AbsolutePath.EndsWith(kind == CollectionKind.Album ? "/album/list" : "/artist/list"));
            Check(request.RequestUri.Query == "?page=2&size=1");
            return Json("""{"code":0,"data":{"list":[{"guid":"collection-a","name":"测试集合"}],"total":3}}""");
        }));
        var result = await api.ListCollectionsAsync(kind, 2, 1, default);
        Check(result.Total == 3 && result.Items.Single().TrackCount is null);
    }
});
await Test("collection detail supports observed envelopes and encodes identifiers", async () =>
{
    foreach (var kind in new[] { CollectionKind.Album, CollectionKind.Artist })
    foreach (bool wrapped in new[] { false, true })
    {
        if (wrapped && kind == CollectionKind.Artist) continue;
        using var api = new NasApiClient(endpoint, new FakeHandler(request =>
        {
            Check(request.RequestUri!.Query == "?guid=a%26b");
            var item = new { guid = "a&b", name = "测试集合", trackCount = 4 };
            return Json(JsonSerializer.Serialize(new { code = 0, data = wrapped ? (object)new { album = item } : item }));
        }));
        var result = await api.GetCollectionAsync(kind, "a&b", default);
        Check(result.Name == "测试集合" && result.TrackCount == 4);
    }
});
await Test("collection tracks use scoped paginated routes and album ordering", async () =>
{
    foreach (var kind in new[] { CollectionKind.Album, CollectionKind.Artist })
    {
        string path = kind == CollectionKind.Album ? "album" : "artist";
        using var api = new NasApiClient(endpoint, new FakeHandler(request =>
        {
            Check(request.RequestUri!.AbsolutePath.EndsWith($"/track/{path}-detail/list"));
            Check(request.RequestUri.Query == $"?{path}GUID=a%26b&page=2&size=1" + (kind == CollectionKind.Album ? "&sort=trackNo%2Casc" : ""));
            return Json("""{"code":0,"data":{"list":[{"guid":"track-a","title":"测试歌曲","duration":61000}],"total":3}}""");
        }));
        var result = await api.ListCollectionTracksAsync(kind, "a&b", 2, 1, default);
        Check(result.Total == 3 && result.Tracks.Single().DurationSeconds == 61);
    }
});
await Test("collection malformed responses and mismatched detail are rejected", async () =>
{
    foreach (var data in new[] { "null", "{}", "{\"list\":[],\"total\":\"0\"}", "{\"list\":[{\"guid\":\"a\",\"name\":\"A\",\"trackCount\":-1}],\"total\":1}", "{\"list\":[{\"guid\":\"a\",\"name\":\"A\"}],\"total\":0}" })
    {
        using var api = new NasApiClient(endpoint, new FakeHandler(_ => Json("{\"code\":0,\"data\":" + data + "}")));
        await Fails(MusicFailure.InvalidResponse, () => api.ListCollectionsAsync(CollectionKind.Album, 1, 1, default));
    }
    using var mismatch = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"guid":"other","name":"A"}}""")));
    await Fails(MusicFailure.InvalidResponse, () => mismatch.GetCollectionAsync(CollectionKind.Artist, "requested", default));
});
await Test("invalid collection arguments send no request and empty lists are valid", async () =>
{
    int calls = 0;
    using var api = new NasApiClient(endpoint, new FakeHandler(_ => { calls++; return Json("""{"code":0,"data":{"list":[],"total":0}}"""); }));
    foreach (var action in new Func<Task>[] {
        () => api.ListCollectionsAsync((CollectionKind)99, 1, 1, default),
        () => api.ListCollectionsAsync(CollectionKind.Album, 0, 1, default),
        () => api.ListCollectionTracksAsync(CollectionKind.Artist, "a", 1, 101, default),
        () => api.GetCollectionAsync(CollectionKind.Album, " ", default) })
    {
        try { await action(); throw new Exception("Accepted invalid argument"); } catch (ArgumentException) { }
    }
    Check(calls == 0);
    Check((await api.ListCollectionsAsync(CollectionKind.Artist, 1, 50, default)).Items.Count == 0);
    Check((await api.ListCollectionTracksAsync(CollectionKind.Album, "a", 1, 50, default)).Tracks.Count == 0);
});
await Test("canonical origin and connection identity", () =>
{
    Check(endpoint.StorageKey == ServerEndpoint.Parse("https://NAS.example.invalid:443").StorageKey);
    Check(endpoint.StorageKey != ServerEndpoint.Parse("http://nas.example.invalid").StorageKey);
    Check(endpoint.ApiUri.AbsolutePath == "/music/api/v1/");
    return Task.CompletedTask;
});
await Test("reject credentials, query, fragments and unrelated paths", () =>
{
    foreach (var input in new[] { "file:///music", "https://user@nas.example.invalid", "https://nas.example.invalid/?code=test", "https://nas.example.invalid/#fragment", "https://nas.example.invalid/admin" })
    {
        try { ServerEndpoint.Parse(input); throw new Exception("Accepted invalid endpoint"); }
        catch (ArgumentException) { }
    }
    return Task.CompletedTask;
});
await Test("connection probe parses initialization status", async () =>
{
    using var api = new NasApiClient(endpoint, new FakeHandler(_ => Json("{\"code\":0,\"data\":{\"initialized\":true}}")));
    Check(await api.CheckConnectionAsync(default));
});
await Test("redirect response is rejected without another request", async () =>
{
    int calls = 0;
    using var api = new NasApiClient(endpoint, new FakeHandler(_ => { calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://other.example.invalid"); return response; }));
    await Fails(MusicFailure.RedirectRejected, () => api.GetCurrentUserAsync(default));
    Check(calls == 1);
});
await Test("401 clears the in-memory session", async () =>
{
    int calls = 0;
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        Check(request.Headers.Contains("Cookie") == (calls == 0));
        calls++;
        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
    }));
    api.RestoreSession(new string('x', 32));
    await Fails(MusicFailure.Unauthorized, () => api.GetCurrentUserAsync(default));
    await Fails(MusicFailure.Unauthorized, () => api.GetCurrentUserAsync(default));
});
await Test("unexpected JSON types remain sanitized API failures", async () =>
{
    using var api = new NasApiClient(endpoint, new FakeHandler(_ => Json("{\"code\":{},\"msg\":\"private response\"}")));
    await Fails(MusicFailure.InvalidResponse, () => api.GetCurrentUserAsync(default));
});
await Test("oversized responses are bounded", async () =>
{
    using var api = new NasApiClient(endpoint, new FakeHandler(_ => Json(new string('x', 2 * 1024 * 1024 + 1))));
    await Fails(MusicFailure.InvalidResponse, () => api.GetCurrentUserAsync(default));
});
await Test("login hashes password and only subsequent requests carry the session", async () =>
{
    int calls = 0;
    string password = new('p', 12);
    string session = new('s', 32);
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        Check(request.RequestUri!.Origin() == endpoint.Origin.AbsoluteUri);
        if (calls++ == 0)
        {
            Check(!request.Headers.Contains("Cookie"));
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Check(body.RootElement.GetProperty("password").GetString() == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password))));
            return Json(JsonSerializer.Serialize(new { code = 0, data = new { userToken = session, user = new { name = "测试账户" } } }));
        }
        Check(request.Headers.GetValues("Cookie").Single() == "music-token=" + session);
        return Json("{\"code\":0,\"data\":{\"name\":\"测试账户\"}}");
    }));
    await api.LoginAsync("account", password, new string('a', 32), default);
    Check((await api.GetCurrentUserAsync(default)).Name == "测试账户");
});
await Test("invalid session data cannot inject request headers", () =>
{
    using var api = new NasApiClient(endpoint);
    try { api.RestoreSession("value\r\nInjected: value"); throw new Exception("Accepted injection"); }
    catch (MusicApiException) { }
    return Task.CompletedTask;
});
if (args.Length < 1) throw new Exception("Provide a dedicated test data directory");
string testDirectory = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"));
await Test("source registry survives restart and separates accounts and servers without plaintext identifiers", async () =>
{
    string directory = Path.Combine(testDirectory, "sources");
    string key = MusicSourceRegistry.AccountKey(endpoint, " sample-account ");
    Check(key == MusicSourceRegistry.AccountKey(ServerEndpoint.Parse("https://nas.example.invalid"), "sample-account"));
    var registry = new MusicSourceRegistry(directory);
    Guid first = await registry.GetOrCreateAsync(key, default);
    Check(first != Guid.Empty && await new MusicSourceRegistry(directory).GetOrCreateAsync(key, default) == first);
    Check(await registry.GetOrCreateAsync(MusicSourceRegistry.AccountKey(endpoint, "another-account"), default) != first);
    Check(await registry.GetOrCreateAsync(MusicSourceRegistry.AccountKey(ServerEndpoint.Parse("https://other.example.invalid"), "sample-account"), default) != first);
    string contents = await File.ReadAllTextAsync(Path.Combine(directory, "sources.json"));
    Check(!contents.Contains("sample-account") && !contents.Contains("example.invalid"));
    var settingsStore = new ConnectionSettingsStore(directory);
    await settingsStore.SaveAsync(new ConnectionSettings(endpoint.MusicUri.AbsoluteUri, Guid.NewGuid().ToString("N"), true) { SourceAccountKey = key }, default);
    Check((await settingsStore.LoadAsync(default))?.SourceAccountKey == key);
});
await Test("source registry corruption and cancellation never replace existing identities", async () =>
{
    string directory = Path.Combine(testDirectory, "bad-sources");
    Directory.CreateDirectory(directory);
    string file = Path.Combine(directory, "sources.json");
    string key = MusicSourceRegistry.AccountKey(endpoint, "account");
    string corrupt = JsonSerializer.Serialize(new Dictionary<string, Guid> { [key] = Guid.Empty });
    await File.WriteAllTextAsync(file, corrupt);
    try { await new MusicSourceRegistry(directory).GetOrCreateAsync(key, default); throw new Exception("Corrupt registry accepted"); }
    catch (InvalidDataException) { }
    Check(await File.ReadAllTextAsync(file) == corrupt);
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await new MusicSourceRegistry(directory).GetOrCreateAsync(key, canceled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
    Check(await File.ReadAllTextAsync(file) == corrupt);
});
await Test("NAS assigns source identity across browsing routes and rejects foreign playback and favorites before transport", async () =>
{
    Guid source = Guid.NewGuid(); int calls = 0;
    using var api = new NasApiClient(endpoint, new FakeHandler(_ =>
    {
        calls++;
        return Json("""{"code":0,"data":{"total":1,"list":[{"guid":"shared"}]}}""");
    }), source);
    var pages = new[] { await api.ListTracksAsync(1, 50, default), await api.SearchTracksAsync("sample", 1, 50, default),
        await api.ListFavoritesAsync(1, 50, default), await api.ListCollectionTracksAsync(CollectionKind.Album, "album", 1, 50, default) };
    Check(pages.All(page => page.Tracks.Single().Reference == new TrackReference(source, "shared")));
    var foreign = new TrackReference(Guid.NewGuid(), "shared");
    await Fails(MusicFailure.Rejected, () => api.OpenTrackStreamAsync(foreign, default));
    await Fails(MusicFailure.Rejected, () => api.SetFavoriteAsync(foreign, true, default));
    Check(calls == 4);
});
await Test("local music scans nested audio files with stable identities and opens through the router", async () =>
{
    string directory = Path.Combine(testDirectory, "local-audio");
    Directory.CreateDirectory(Path.Combine(directory, "album"));
    await File.WriteAllBytesAsync(Path.Combine(directory, "song.WAV"), [1, 2, 3]);
    await File.WriteAllBytesAsync(Path.Combine(directory, "album", "song.mp3"), [4, 5]);
    await File.WriteAllTextAsync(Path.Combine(directory, "ignore.txt"), "not audio");
    var settings = new LocalMusicSettings(Guid.NewGuid(), directory);
    var source = await LocalMusicSource.ScanAsync(settings, default);
    Check(source.Library.SourceInstanceId == settings.SourceId && source.Tracks.All(track => track.GetLibraries().Single() == new LibraryReference(settings.SourceId, source.Library.Id)));
    Check(source.Source.Capabilities.HasFlag(MusicSourceCapabilities.Artwork | MusicSourceCapabilities.Lyrics));
    Check(!source.Source.Capabilities.HasFlag(MusicSourceCapabilities.WriteFavorites)
        && !source.Source.Capabilities.HasFlag(MusicSourceCapabilities.WriteSettings));
    Check(source.Tracks.Count == 2 && source.Tracks.All(track => track.SourceInstanceId == settings.SourceId));
    var again = await LocalMusicSource.ScanAsync(settings, default);
    Check(source.Tracks.Select(t => t.Reference).SequenceEqual(again.Tracks.Select(t => t.Reference)));
    var sidecarTrack = source.Tracks.First(t => source.GetFilePath(t.Reference).EndsWith("song.WAV", StringComparison.OrdinalIgnoreCase));
    await File.WriteAllTextAsync(Path.Combine(directory, "song.lrc"), "[00:01.50]本地歌词");
    await File.WriteAllBytesAsync(Path.Combine(directory, "song.png"), [7, 8]);
    Check((await source.GetLyricsAsync(sidecarTrack.Reference, default)).Lines.Single() == new LyricLine(1.5, "本地歌词"));
    await File.WriteAllBytesAsync(Path.Combine(directory, "song.lrc"), CodePagesEncodingProvider.Instance.GetEncoding(54936)!.GetBytes("[00:02]传统编码歌词"));
    Check((await source.GetLyricsAsync(sidecarTrack.Reference, default)).Lines.Single() == new LyricLine(2, "传统编码歌词"));
    Check((await source.GetSidecarCoverAsync(sidecarTrack.Reference, default))!.SequenceEqual(new byte[] { 7, 8 }));
    await File.WriteAllBytesAsync(Path.Combine(directory, "song.jpg"), new byte[4 * 1024 * 1024 + 1]);
    Check((await source.GetSidecarCoverAsync(sidecarTrack.Reference, default))!.SequenceEqual(new byte[] { 7, 8 }));
    using (var canceledCover = new CancellationTokenSource())
    {
        canceledCover.Cancel();
        try { await source.GetSidecarCoverAsync(sidecarTrack.Reference, canceledCover.Token); throw new Exception("Canceled cover accepted"); }
        catch (OperationCanceledException) { }
    }
    await Fails(MusicFailure.Rejected, () => source.GetLyricsAsync(new(Guid.NewGuid(), sidecarTrack.Id), default));
    var router = new MusicPlaybackRouter(); router.Register(source);
    using (var stream = await router.OpenAsync(source.Tracks.First().Reference, default)) Check(stream.CanSeek && stream.ReadByte() is 1 or 4);
    await Fails(MusicFailure.Rejected, () => source.OpenAsync(new(settings.SourceId, "../ignore.txt"), default));
    await Fails(MusicFailure.Rejected, () => source.OpenAsync(new(Guid.NewGuid(), source.Tracks[0].Id), default));
    var store = new LocalMusicSettingsStore(Path.Combine(testDirectory, "local-config"));
    await store.SaveAsync(settings, default);
    Check(await store.LoadAsync(default) == settings);
    File.Delete(Path.Combine(directory, "song.WAV"));
    Check((await LocalMusicSource.ScanAsync(settings, default)).Tracks.Count == 1 && source.Tracks.Count == 2);
    bool missing = false;
    foreach (var track in source.Tracks)
        try { using var stream = await source.OpenAsync(track.Reference, default); }
        catch (FileNotFoundException) { missing = true; }
    Check(missing);
    router.Unregister(settings.SourceId);
});
await Test("local lyrics decode Unicode and legacy Chinese encodings without replacement characters", () =>
{
    const string content = "[00:01.25]中文歌词与翻译\n[00:03]第二句";
    var legacy = CodePagesEncodingProvider.Instance.GetEncoding(54936)!;
    foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true), legacy })
    {
        byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
        string decoded = LyricsTextDecoder.Decode(bytes);
        Check(decoded == content && Lyrics.Parse(decoded).Lines.Count == 2 && Lyrics.Parse(decoded).Lines[0].Seconds == 1.25);
    }
    foreach (byte[] invalid in new byte[][] { [0xFF], [0xEF, 0xBB, 0xBF, 0xFF], [0xFF, 0xFE, 0], [0], new byte[512001] })
    {
        try { LyricsTextDecoder.Decode(invalid); throw new Exception("Invalid lyrics accepted"); }
        catch (DecoderFallbackException) { }
        catch (InvalidDataException) { }
    }
    return Task.CompletedTask;
});

await Test("local lyric files preserve encoding, newline, offset and bilingual timeline combinations", async () =>
{
    string root = Path.Combine(testDirectory, "lyric-matrix");
    Directory.CreateDirectory(root);
    string audioFile = Path.Combine(root, "fixture.wav"), lyricFile = Path.Combine(root, "fixture.lrc");
    try
    {
        await File.WriteAllBytesAsync(audioFile, [1]);
        var source = await LocalMusicSource.ScanAsync(new(Guid.NewGuid(), root), default,
            (_, _) => Task.FromResult(new LocalMusicMetadata("fixture", "fixture", 20)));
        var reference = source.Tracks.Single().Reference;
        Encoding[] encodings = [new UTF8Encoding(false, true), new UTF8Encoding(true, true),
            new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
            CodePagesEncodingProvider.Instance.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!,
            CodePagesEncodingProvider.Instance.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!];
        foreach (var encoding in encodings)
        foreach (string newline in new[] { "\r", "\n", "\r\n" })
        {
            foreach (int offset in new[] { -200, 0, 150 })
            {
                string text = string.Join(newline, "[ar:fixture]", $"[offset:{offset}]",
                    "[00:01.05][00:03:125]第一句", "[00:01.05]翻译", "[00:10]尾句");
                await File.WriteAllBytesAsync(lyricFile, encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
                var actual = await source.GetLyricsAsync(reference, default);
                Check(actual.IsTimed && actual.Lines.Count == 3 && actual.Lines[0].Text == "第一句\n翻译"
                    && actual.Lines[1].Text == "第一句" && actual.Lines[2].Text == "尾句");
                double[] times = [1.05 - offset / 1000d, 3.125 - offset / 1000d, 10 - offset / 1000d];
                for (int i = 0; i < times.Length; i++)
                {
                    Check(Math.Abs(actual.Lines[i].Seconds - times[i]) < 1e-9);
                    Check(actual.FindLine(actual.Lines[i].Seconds - .001) == i - 1 && actual.FindLine(actual.Lines[i].Seconds) == i);
                }
            }
            string plain = string.Join(newline, "[ti:fixture]", "第一句", "第二句");
            await File.WriteAllBytesAsync(lyricFile, encoding.GetPreamble().Concat(encoding.GetBytes(plain)).ToArray());
            var plainLyrics = await source.GetLyricsAsync(reference, default);
            Check(!plainLyrics.IsTimed && plainLyrics.Lines.Select(line => line.Text).SequenceEqual(new[] { "第一句", "第二句" }));
        }
    }
    finally
    {
        File.Delete(lyricFile); File.Delete(audioFile); Directory.Delete(root);
    }
});
await Test("local incremental scans read changed metadata and retain unchanged identities", async () =>
{
    string root = Path.Combine(Path.GetTempPath(), "fnmusic-incremental-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        string first = Path.Combine(root, "first.wav"), second = Path.Combine(root, "second.wav");
        await File.WriteAllBytesAsync(first, [1]); await File.WriteAllBytesAsync(second, [2]);
        var settings = new LocalMusicSettings(Guid.NewGuid(), root);
        int reads = 0;
        Task<LocalMusicMetadata> Read(string path, CancellationToken ct)
        { reads++; return Task.FromResult(new LocalMusicMetadata(Path.GetFileNameWithoutExtension(path), "artist", 10)); }
        var initial = await LocalMusicSource.ScanAsync(settings, default, Read);
        Check(reads == 2);
        var unchanged = await LocalMusicSource.ScanAsync(settings, default, Read, previous: initial);
        Check(reads == 2 && unchanged.Tracks.Select(t => t.Reference).SequenceEqual(initial.Tracks.Select(t => t.Reference)));
        await File.AppendAllTextAsync(first, "changed");
        var changed = await LocalMusicSource.ScanAsync(settings, default, Read, previous: unchanged);
        Check(reads == 3 && changed.Tracks.Count == 2);
        File.Delete(second); await File.WriteAllBytesAsync(Path.Combine(root, "third.wav"), [3]);
        var refreshed = await LocalMusicSource.ScanAsync(settings, default, Read, previous: changed);
        Check(reads == 4 && refreshed.Tracks.Count == 2 && refreshed.Tracks.All(t => t.Title != "second"));
        Check(refreshed.Tracks.Single(t => t.Title == "first").Reference == initial.Tracks.Single(t => t.Title == "first").Reference);
        await LocalMusicSource.ScanAsync(settings with { SourceId = Guid.NewGuid() }, default, Read, previous: refreshed);
        Check(reads == 6);
    }
    finally
    {
        foreach (var file in Directory.GetFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }
});

await Test("local library at 10000 files retains its snapshot across overflow and canceled refresh", async () =>
{
    string root = Path.Combine(testDirectory, "large-library-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var createdFiles = new List<string>();
    var createdDirectories = new List<string>();
    try
    {
        // Real directory enumeration; tag reads are controlled, not codec acceptance.
        for (int folder = 0; folder < 100; folder++)
        {
            string directory = Path.Combine(root, folder.ToString("D3"));
            Directory.CreateDirectory(directory); createdDirectories.Add(directory);
            for (int song = 0; song < 100; song++)
            {
                string path = Path.Combine(directory, $"song-{song:D3}.wav");
                createdFiles.Add(path);
                await File.WriteAllBytesAsync(path, [1]);
            }
        }
        int reads = 0;
        Task<LocalMusicMetadata> Read(string path, CancellationToken ct)
        { reads++; return Task.FromResult(new LocalMusicMetadata(Path.GetFileNameWithoutExtension(path), "fixture", 1)); }
        var settings = new LocalMusicSettings(Guid.NewGuid(), root);
        var progress = new List<int>();
        var initial = await LocalMusicSource.ScanAsync(settings, default, Read, new InlineProgress<int>(progress.Add));
        Check(initial.Tracks.Count == 10000 && reads == 10000);
        Check(initial.Tracks.Select(t => t.Reference).Distinct().Count() == 10000);
        Check(progress.First() == 0 && progress.Last() == 10000 && progress.SequenceEqual(progress.Order()));
        var originalReferences = initial.Tracks.Select(t => t.Reference).ToArray();
        var unchanged = await LocalMusicSource.ScanAsync(settings, default, Read, previous: initial);
        Check(reads == 10000 && unchanged.Tracks.Select(t => t.Reference).SequenceEqual(originalReferences));
        await File.AppendAllTextAsync(createdFiles[0], "changed");
        var changed = await LocalMusicSource.ScanAsync(settings, default, Read, previous: unchanged);
        Check(reads == 10001 && changed.Tracks.Select(t => t.Reference).SequenceEqual(originalReferences));
        string extra = Path.Combine(root, "overflow.wav"); createdFiles.Add(extra);
        await File.WriteAllBytesAsync(extra, [2]);
        try
        {
            await LocalMusicSource.ScanAsync(settings, default, Read, previous: changed);
            throw new Exception("Oversized library accepted");
        }
        catch (InvalidDataException) { }
        Check(changed.Tracks.Select(t => t.Reference).SequenceEqual(originalReferences));
        File.Delete(extra);
        using var cancel = new CancellationTokenSource();
        try
        {
            await LocalMusicSource.ScanAsync(settings, cancel.Token, Read,
                new InlineProgress<int>(count => { if (count >= 5000) cancel.Cancel(); }), previous: changed);
            throw new Exception("Canceled refresh accepted");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Check(changed.Tracks.Select(t => t.Reference).SequenceEqual(originalReferences));
        var recovered = await LocalMusicSource.ScanAsync(settings, default, Read, previous: changed);
        Check(recovered.Tracks.Select(t => t.Reference).SequenceEqual(originalReferences));
        using var stream = await recovered.OpenAsync(recovered.Tracks[0].Reference, default);
        Check(stream.ReadByte() == 1);
    }
    finally
    {
        // Delete only fixture paths recorded above; never recursively delete a computed root.
        foreach (string file in createdFiles) File.Delete(file);
        foreach (string directory in createdDirectories) Directory.Delete(directory);
        Directory.Delete(root);
    }
});

await Test("local metadata enriches tracks without changing references and falls back on invalid tags", async () =>
{
    string directory = Path.Combine(testDirectory, "metadata"); Directory.CreateDirectory(directory);
    await File.WriteAllBytesAsync(Path.Combine(directory, "one.wav"), [1]);
    var settings = new LocalMusicSettings(Guid.NewGuid(), directory);
    var plain = await LocalMusicSource.ScanAsync(settings, default);
    var enriched = await LocalMusicSource.ScanAsync(settings, default, (_, _) => Task.FromResult(new LocalMusicMetadata("Title", "Artist", 42)));
    Check(enriched.Tracks[0].Reference == plain.Tracks[0].Reference && enriched.Tracks[0].Title == "Title" && enriched.Tracks[0].Artist == "Artist" && enriched.Tracks[0].DurationSeconds == 42);
    var bad = await LocalMusicSource.ScanAsync(settings, default, (_, _) => Task.FromResult(new LocalMusicMetadata("", "", double.NaN)));
    Check(bad.Tracks[0].Title == "one" && bad.Tracks[0].DurationSeconds == 0);
    var failed = await LocalMusicSource.ScanAsync(settings, default, (_, _) => throw new IOException());
    Check(failed.Tracks[0].Reference == plain.Tracks[0].Reference && failed.Tracks[0].Title == "one");
});
await Test("local scan reports progress and cancellation during metadata prevents a partial result", async () =>
{
    string directory = Path.Combine(testDirectory, "scan-progress"); Directory.CreateDirectory(directory);
    for (int i = 0; i < 26; i++) await File.WriteAllBytesAsync(Path.Combine(directory, $"{i}.wav"), [1]);
    var reports = new List<int>();
    var settings = new LocalMusicSettings(Guid.NewGuid(), directory);
    var result = await LocalMusicSource.ScanAsync(settings, default, progress: new InlineProgress<int>(reports.Add));
    Check(result.Tracks.Count == 26 && reports.SequenceEqual(new[] { 0, 25, 26 }));
    using var cancel = new CancellationTokenSource();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = LocalMusicSource.ScanAsync(settings, cancel.Token, async (_, ct) =>
    { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new("", "", 0); });
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
    try { await pending; throw new Exception("Partial result accepted"); } catch (OperationCanceledException) { }
    Check(result.Tracks.Count == 26);
});
await Test("local scan honors cancellation and rejects missing roots", async () =>
{
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await LocalMusicSource.ScanAsync(new(Guid.NewGuid(), testDirectory), canceled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
    try { await LocalMusicSource.ScanAsync(new(Guid.NewGuid(), Path.Combine(testDirectory, "missing")), default); throw new Exception("Missing accepted"); }
    catch (DirectoryNotFoundException) { }
});
await Test("first login can clear a session before its directory exists", async () =>
{
    var vault = new DpapiSessionVault(Path.Combine(testDirectory, "not-created"));
    await vault.ClearAsync(endpoint.StorageKey, default);
    await vault.ClearAsync(endpoint.StorageKey, default);
    Check(await vault.LoadAsync(endpoint.StorageKey, default) is null);
});
if (args.Contains("--skip-dpapi")) Console.WriteLine("SKIP DPAPI: explicitly excluded for the sandbox user; requires normal Windows profile.");
else await Test("DPAPI stores encrypted data and restores only for the matching connection", async () =>
{
    var vault = new DpapiSessionVault(testDirectory);
    string session = new('q', 32);
    await vault.SaveAsync(endpoint.StorageKey, session, default);
    Check(await vault.LoadAsync(endpoint.StorageKey, default) == session);
    var file = Path.Combine(testDirectory, endpoint.StorageKey + ".bin");
    Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains(session));
    string other = ServerEndpoint.Parse("https://other.example.invalid").StorageKey;
    File.Copy(file, Path.Combine(testDirectory, other + ".bin"));
    try { await vault.LoadAsync(other, default); throw new Exception("Origin binding failed"); }
    catch (CryptographicException) { }
    await vault.ClearAsync(endpoint.StorageKey, default);
    await vault.ClearAsync(other, default);
    Check(await vault.LoadAsync(endpoint.StorageKey, default) is null);
});
await Test("connection settings survive UTF-8 round trip", async () =>
{
    var store = new ConnectionSettingsStore(testDirectory);
    var settings = new ConnectionSettings(endpoint.MusicUri.AbsoluteUri, new string('a', 32), true);
    await store.SaveAsync(settings, default);
    Check(await store.LoadAsync(default) == settings);
});
await Test("track page maps milliseconds and artists", async () =>
{
    using var api = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"total":1,"list":[{"guid":"test-track","title":"测试","artists":[{"name":"甲"},{"name":"乙"}],"duration":125000,"isCue":true}]}}""")));
    var result = await api.ListTracksAsync(1, 50, default);
    Check(result.Total == 1 && result.Tracks[0].DurationSeconds == 125);
    Check(result.Tracks[0].Artist == "甲 / 乙" && result.Tracks[0].DisplayDuration == "2:05" && result.Tracks[0].IsCue);
});
await Test("track page rejects malformed total without exposing response", async () =>
{
    using var api = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"total":"private","list":[]}}""")));
    await Fails(MusicFailure.InvalidResponse, () => api.ListTracksAsync(1, 50, default));
});
await Test("search encodes query and preserves pagination and track mapping", async () =>
{
    using var api = new NasApiClient(endpoint, new FakeHandler(request =>
    {
        Check(request.RequestUri!.AbsolutePath == "/music/api/v1/search/track");
        Check(request.RequestUri.Query == "?q=" + Uri.EscapeDataString("中文 &?#+/歌曲") + "&page=2&size=25");
        return Json("""{"code":0,"data":{"total":26,"list":[{"guid":"search-track","title":"测试","duration":90000}]}}""");
    }));
    var found = await api.SearchTracksAsync("  中文 &?#+/歌曲  ", 2, 25, default);
    Check(found.Total == 26 && found.Tracks[0].Id == "search-track" && found.Tracks[0].DurationSeconds == 90);
});
await Test("search paginates complete results from servers ignoring page and size", async () =>
{
    using var api = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"total":3,"list":[{"guid":"a"},{"guid":"b"},{"guid":"c"}]}}""")));
    Check((await api.SearchTracksAsync("test", 1, 1, default)).Tracks.Single().Id == "a");
    Check((await api.SearchTracksAsync("test", 2, 1, default)).Tracks.Single().Id == "b");
    var last = await api.SearchTracksAsync("test", 2, 2, default);
    Check(last.Tracks.Single().Id == "c" && last.Total == 3);
    Check((await api.SearchTracksAsync("test", 4, 1, default)).Tracks.Count == 0);
    Check((await api.SearchTracksAsync("test", int.MaxValue, 100, default)).Tracks.Count == 0);
});
await Test("search preserves server pages and rejects ambiguous oversized partial results", async () =>
{
    using var paged = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"total":3,"list":[{"guid":"b"}]}}""")));
    Check((await paged.SearchTracksAsync("test", 2, 1, default)).Tracks.Single().Id == "b");
    using var oversized = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"total":3,"list":[{"guid":"a"},{"guid":"b"}]}}""")));
    await Fails(MusicFailure.InvalidResponse, () => oversized.SearchTracksAsync("test", 1, 1, default));
    using var inconsistent = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"total":0,"list":[{"guid":"a"}]}}""")));
    await Fails(MusicFailure.InvalidResponse, () => inconsistent.SearchTracksAsync("test", 1, 50, default));
});
await Test("search rejects invalid input before HTTP and accepts empty result", async () =>
{
    int calls = 0;
    using var api = new NasApiClient(endpoint, new FakeHandler(_ => { calls++; return Json("""{"code":0,"data":{"total":0,"list":[]}}"""); }));
    foreach (var query in new[] { " ", new string('x',257) })
    {
        try { await api.SearchTracksAsync(query, 1, 50, default); throw new Exception("Invalid query accepted"); }
        catch (ArgumentException) { }
    }
    try { await api.SearchTracksAsync("test", 0, 50, default); throw new Exception("Invalid page accepted"); }
    catch (ArgumentOutOfRangeException) { }
    Check(calls == 0);
    var found = await api.SearchTracksAsync("test", 1, 50, default);
    Check(found.Total == 0 && found.Tracks.Count == 0 && calls == 1);
});
await Test("search handles expired sessions and malformed response", async () =>
{
    using var expired = new NasApiClient(endpoint, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
    await Fails(MusicFailure.Unauthorized, () => expired.SearchTracksAsync("test", 1, 50, default));
    using var malformed = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"total":1,"list":[{"title":"private"}]}}""")));
    await Fails(MusicFailure.InvalidResponse, () => malformed.SearchTracksAsync("test", 1, 50, default));
});
await Test("range stream seeks across cache blocks and reaches EOF", async () =>
{
    byte[] song = Enumerable.Range(0, 600000).Select(i => (byte)(i % 251)).ToArray();
    int calls = 0;
    var handler = new FakeHandler(request =>
    {
        calls++;
        var range = request.Headers.Range!.Ranges.Single();
        long from = range.From!.Value, to = range.To!.Value;
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(song[(int)from..((int)to + 1)]) };
        response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, song.Length);
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/flac");
        return response;
    });
    using var stream = await HttpRangeStream.OpenAsync(new HttpClient(handler), endpoint.MusicUri, default);
    byte[] actual = new byte[1024];
    Check(await stream.ReadAsync(actual) == actual.Length && actual.SequenceEqual(song[..1024]));
    stream.Seek(4096, SeekOrigin.Begin);
    Check(await stream.ReadAsync(actual) == actual.Length && actual.SequenceEqual(song[4096..5120]) && calls == 2);
    stream.Seek(300000, SeekOrigin.Begin);
    Check(await stream.ReadAsync(actual) == actual.Length && actual.SequenceEqual(song[300000..301024]) && calls == 3);
    stream.Seek(-10, SeekOrigin.End);
    Check(await stream.ReadAsync(actual) == 10 && actual[..10].SequenceEqual(song[^10..]));
    Check(await stream.ReadAsync(actual) == 0);
    stream.Dispose();
    try { _ = await stream.ReadAsync(actual); throw new Exception("Disposed stream accepted read"); }
    catch (ObjectDisposedException) { }
});
await Test("range stream rejects redirects and servers ignoring Range", async () =>
{
    foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.OK, HttpStatusCode.Unauthorized })
    {
        var failure = status == HttpStatusCode.Redirect ? MusicFailure.RedirectRejected : status == HttpStatusCode.Unauthorized ? MusicFailure.Unauthorized : MusicFailure.InvalidResponse;
        await Fails(failure, async () => { using var stream = await HttpRangeStream.OpenAsync(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(status))), endpoint.MusicUri, default); });
    }
});
await Test("range stream rejects a file changing size between requests", async () =>
{
    int calls = 0;
    var handler = new FakeHandler(request =>
    {
        var range = request.Headers.Range!.Ranges.Single();
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[100]) };
        response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(range.From!.Value, range.To!.Value, calls++ == 0 ? 100 : 101);
        return response;
    });
    using var stream = await HttpRangeStream.OpenAsync(new HttpClient(handler), endpoint.MusicUri, default);
    await Fails(MusicFailure.InvalidResponse, async () => { _ = await stream.ReadAsync(new byte[10]); });
});
await VerifyRangeCacheAsync();
await Test("range stream rejects changed, weak or missing pinned versions without publishing bytes", RangeRepresentationVerification.ChangedVersionAsync);
await Test("range stream binds strong representation versions across cache blocks", RangeRepresentationVerification.StrongVersionAsync);
await Test("range stream preserves weak or absent validator compatibility without conditional requests", RangeRepresentationVerification.UnavailableVersionAsync);
await Test("range stream discards a completed block after caller cancellation", RangeStreamVerification.CallerCancellationAsync);
await Test("range stream discards a completed block after disposal", RangeStreamVerification.DisposalAsync);
await Test("range stream disposal wakes a seek waiting behind a delayed read", RangeStreamVerification.QueuedSeekAsync);
await Test("range stream disposal cancels a partial body over real loopback TCP", LoopbackRangeVerification.DisposalAsync);
await Test("range stream body timeout preserves position and allows TCP retry", LoopbackRangeVerification.BodyTimeoutAsync);
await Test("range stream open timeout closes a stalled TCP response", LoopbackRangeVerification.OpenTimeoutAsync);
if (args.Contains("--ffmpeg"))
{
    foreach (string format in new[] { "wav", "flac", "mp3", "ogg" })
    {
        await Test("FFmpeg decodes synthetic " + format + " and seeks by decoded timestamp", async () =>
        {
            // 全部测试音频由本机生成，不接触用户歌曲或网络。
            var info = new System.Diagnostics.ProcessStartInfo(FfmpegPcmDecoder.FindExecutable())
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-f", format, "pipe:1" }) info.ArgumentList.Add(arg);
            using var encoder = System.Diagnostics.Process.Start(info)!;
            using var encoded = new MemoryStream();
            var errors = encoder.StandardError.ReadToEndAsync();
            await encoder.StandardOutput.BaseStream.CopyToAsync(encoded);
            await encoder.WaitForExitAsync(); await errors;
            Check(encoder.ExitCode == 0 && encoded.Length > 0);
            encoded.Position = 0;
            await using (var bufferedDecoder = new FfmpegPcmDecoder(encoded, TimeSpan.Zero))
            await using (var cache = new PcmReadAheadBuffer(bufferedDecoder.Output))
            {
                byte[] sample = new byte[4096];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                Check(await cache.ReadAtAsync(FfmpegPcmDecoder.BytesPerSecond, sample, timeout.Token) > 0);
                Check(await cache.ReadAtAsync(0, sample, timeout.Token) > 0);
                Check(sample.Any(b => b != 0));
            }
            foreach (int start in new[] { 0, 1 })
            {
                encoded.Position = 0;
                await using var decoder = new FfmpegPcmDecoder(encoded, TimeSpan.FromSeconds(start));
                using var pcm = new MemoryStream();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await decoder.Output.CopyToAsync(pcm, timeout.Token);
                await decoder.CheckCompletionAsync(timeout.Token);
                double seconds = (double)pcm.Length / FfmpegPcmDecoder.BytesPerSecond;
                Check(Math.Abs(seconds - (2 - start)) < 0.15);
                Check(pcm.ToArray().Any(b => b != 0));
            }
        });
    }
    await Test("FFmpeg rejects malformed input with sanitized failure", async () =>
    {
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("not music"));
        await using var decoder = new FfmpegPcmDecoder(invalid, TimeSpan.Zero);
        await decoder.Output.CopyToAsync(Stream.Null);
        try { await decoder.CheckCompletionAsync(default); throw new Exception("Invalid audio accepted"); }
        catch (IOException error) { Check(error.Message == "Audio decoding failed."); }
    });
    await Test("FFmpeg stops when PCM output is not consumed and cleanup is repeatable", async () =>
    {
        using var wave = new MemoryStream();
        using (var writer = new BinaryWriter(wave, Encoding.UTF8, leaveOpen: true))
        {
            const int dataSize = 48000 * 4 * 20;
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(dataSize + 36);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)2); writer.Write(48000);
            writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataSize); writer.Write(new byte[dataSize]);
        }
        wave.Position = 0;
        var decoder = new FfmpegPcmDecoder(wave, TimeSpan.Zero);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await decoder.Output.ReadExactlyAsync(new byte[16], timeout.Token);
            await decoder.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await decoder.DisposeAsync();
            Check(wave.CanRead); // 解码器不拥有输入，播放源在清理结束后释放它。
        }
        finally { await decoder.DisposeAsync(); }
    });
}
MusicTrack MakeTrack(string id, bool cue = false) => new(id, "测试曲目", "测试歌手", 60, cue);
await Test("next requests preserve current identity and deduplicate by source reference", () =>
{
    var queue = new PlaybackQueue();
    var first = MakeTrack("a"); var second = MakeTrack("b"); var third = MakeTrack("c");
    queue.Replace([first, second, third], second.Reference);
    Check(queue.InsertNext(first) && queue.Current?.Reference == second.Reference && queue.Index == 0 && queue.Count == 3);
    Check(queue.Tracks[1].Reference == first.Reference && queue.InsertNext(first) && queue.Count == 3);
    var foreign = first with { SourceInstanceId = Guid.NewGuid() };
    Check(queue.InsertNext(foreign) && queue.Count == 4 && queue.Current?.Reference == second.Reference);
    Check(queue.Next(true)?.Reference == foreign.Reference && queue.Next(true)?.Reference == first.Reference);
    Check(!queue.InsertNext(first) && !queue.InsertNext(MakeTrack("cue", true)) && queue.Count == 4);
    return Task.CompletedTask;
});
await Test("explicit next requests override shuffle and repeat-one then resume the mode", () =>
{
    foreach (var mode in new[] { QueueMode.Shuffle, QueueMode.RepeatOne })
    {
        var queue = new PlaybackQueue(new Random(42)) { Mode = mode };
        queue.Replace([MakeTrack("a"), MakeTrack("b"), MakeTrack("c")], MakeTrack("a").Reference);
        queue.InsertNext(MakeTrack("c")); queue.InsertNext(MakeTrack("b"));
        Check(queue.Next(true)?.Id == "b" && queue.Next(false)?.Id == "c");
        Check(queue.Previous()?.Id == "b" && queue.Select(MakeTrack("c").Reference)?.Id == "c");
        Check(mode == QueueMode.RepeatOne ? queue.Next(true)?.Id == "c" : queue.Next(true)?.Id != "c");
    }
    return Task.CompletedTask;
});
await Test("queued next requests are cleared by replacement and removal", () =>
{
    var queue = new PlaybackQueue { Mode = QueueMode.RepeatOne };
    queue.Replace([MakeTrack("a"), MakeTrack("b")], MakeTrack("a").Reference);
    queue.InsertNext(MakeTrack("b")); queue.Remove(MakeTrack("b").Reference);
    Check(queue.Next(true)?.Id == "a");
    queue.InsertNext(MakeTrack("b")); queue.Replace([MakeTrack("a"), MakeTrack("b")], MakeTrack("a").Reference);
    Check(queue.Next(true)?.Id == "a");
    queue.Clear(); Check(queue.InsertNext(MakeTrack("b")) && queue.Current?.Id == "b" && queue.Count == 1);
    queue.Clear(); Check(queue.Count == 0 && queue.Next(true) is null);
    return Task.CompletedTask;
});
await Test("track related collections use distinct provider identifiers rather than names", async () =>
{
    using var api = new NasApiClient(endpoint, new FakeHandler(_ => Json("""{"code":0,"data":{"total":1,"list":[{"guid":"track","artists":[{"guid":"a","name":"同名"},{"guid":"b","name":"同名"},{"guid":"a","name":"重复"},{"name":"无ID"}],"album":{"guid":"album-id","name":"专辑"}}]}}""")));
    var track = (await api.ListTracksAsync(1, 50, default)).Tracks.Single();
    Check(track.Artists.Count == 2 && track.Artists[0].Id == "a" && track.Artists[1].Id == "b" && track.AlbumId == "album-id" && track.Album == "专辑");
});
await Test("local playlist snapshots retain related collection identities and reject malformed references", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "fnmusic-related-" + Guid.NewGuid().ToString("N"));
    var store = new LocalPlaylistStore(directory);
    var track = MakeTrack("related") with { SourceInstanceId = Guid.NewGuid(), AlbumId = "album", Artists = [new("a", "同名"), new("b", "同名")] };
    var entry = LocalPlaylistEntry.FromTrack(track);
    try
    {
        var saved = await store.SaveAsync(new(0, [new(Guid.NewGuid(), "fixture", [entry])]));
        var restored = (await store.LoadAsync()).Playlists.Single().Entries.Single();
        Check(restored == entry && restored.GetHashCode() == entry.GetHashCode() && restored.ToTrack().AlbumId == track.AlbumId
            && restored.ToTrack().Artists.SequenceEqual(track.Artists));
        foreach (var invalid in new[] { entry with { AlbumId = "" }, entry with { Artists = [new("", "name")] }, entry with { Artists = [new("same", "a"), new("same", "b")] } })
        {
            try { await store.SaveAsync(saved with { Playlists = [saved.Playlists[0] with { Entries = [invalid] }] }); throw new Exception("Malformed related reference saved"); }
            catch (InvalidDataException) { }
        }
        Check((await store.LoadAsync()).Revision == saved.Revision);
    }
    finally { Directory.Delete(directory, true); }
});
await Test("queue snapshot excludes unsupported tracks and survives source mutation", () =>
{
    var source = new List<MusicTrack> { MakeTrack("a"), MakeTrack("cue", true), MakeTrack("b"), MakeTrack("a") };
    var queue = new PlaybackQueue();
    Check(queue.Replace(source, MakeTrack("a").Reference) && queue.Count == 2);
    source.Clear();
    Check(queue.Next(true)?.Id == "b" && queue.Next(true) is null);
    Check(queue.Previous()?.Id == "a");
    return Task.CompletedTask;
});
await Test("queue distinguishes automatic repeat-one from manual next", () =>
{
    var queue = new PlaybackQueue { Mode = QueueMode.RepeatOne };
    queue.Replace(new[] { MakeTrack("a"), MakeTrack("b") }, MakeTrack("a").Reference);
    Check(queue.Next(true)?.Id == "a");
    Check(queue.Next(false)?.Id == "b" && queue.Next(false)?.Id == "a");
    queue.Mode = QueueMode.RepeatAll;
    Check(queue.Previous()?.Id == "b");
    return Task.CompletedTask;
});
await Test("shuffle avoids immediate repeats and previous restores playback history", () =>
{
    var queue = new PlaybackQueue(new Random(42)) { Mode = QueueMode.Shuffle };
    queue.Replace(new[] { MakeTrack("a"), MakeTrack("b"), MakeTrack("c") }, MakeTrack("a").Reference);
    for (int i = 0; i < 20; i++)
    {
        string before = queue.Current!.Id;
        Check(queue.Next(true)?.Id != before);
        Check(queue.Previous()?.Id == before);
    }
    return Task.CompletedTask;
});
await Test("queue clear prevents cross-account continuation", () =>
{
    var queue = new PlaybackQueue();
    queue.Replace(new[] { MakeTrack("a") }, MakeTrack("a").Reference); queue.Clear();
    Check(queue.Current is null && queue.Count == 0 && queue.Next(true) is null && queue.Previous() is null);
    return Task.CompletedTask;
});
await Test("queue removal preserves identity and discards stale shuffle history", () =>
{
    var queue = new PlaybackQueue(new Random(42)) { Mode = QueueMode.Shuffle };
    queue.Replace(new[] { MakeTrack("a"), MakeTrack("b"), MakeTrack("c") }, MakeTrack("b").Reference);
    Check(queue.Select(MakeTrack("c").Reference)?.Id == "c");
    Check(queue.Remove(MakeTrack("a").Reference) && queue.Current?.Id == "c" && queue.Index == 1);
    Check(queue.Previous()?.Id == "b");
    Check(!queue.Remove(MakeTrack("missing").Reference) && queue.Count == 2);
    Check(queue.Select(MakeTrack("missing").Reference) is null && queue.Current?.Id == "b");
    Check(queue.Remove(MakeTrack("b").Reference) && queue.Current is null && queue.Next(true) is null);
    Check(queue.Select(MakeTrack("c").Reference)?.Id == "c");
    Check(queue.Remove(MakeTrack("c").Reference) && queue.Count == 0 && queue.Previous() is null);
    return Task.CompletedTask;
});
await Test("invalid queue replacement preserves the active snapshot", () =>
{
    var queue = new PlaybackQueue();
    queue.Replace(new[] { MakeTrack("a") }, MakeTrack("a").Reference);
    Check(!queue.Replace(new[] { MakeTrack("cue", true) }, MakeTrack("cue").Reference) && queue.Current?.Id == "a");
    queue.Mode = QueueMode.Shuffle;
    Check(queue.Next(true) is null);
    return Task.CompletedTask;
});
await Test("queue keeps same provider IDs from separate sources and removes only the selected reference", () =>
{
    var first = MakeTrack("shared") with { SourceInstanceId = Guid.NewGuid() };
    var second = first with { SourceInstanceId = Guid.NewGuid() };
    var queue = new PlaybackQueue();
    Check(queue.Replace([first, second, first with { Title = "renamed" }], second.Reference));
    Check(queue.Count == 2 && queue.Current?.Reference == second.Reference);
    Check(queue.Select(first.Reference)?.Reference == first.Reference);
    Check(queue.Previous()?.Reference == second.Reference);
    Check(queue.Remove(first.Reference) && queue.Count == 1 && queue.Current?.Reference == second.Reference);
    Check(!queue.Remove(first.Reference) && queue.Select(first.Reference) is null);
    Check(queue.Remove(second.Reference) && queue.Current is null);
    return Task.CompletedTask;
});
await Test("queue rejects a foreign source reference without replacing the snapshot", () =>
{
    var first = MakeTrack("shared") with { SourceInstanceId = Guid.NewGuid() };
    var foreign = first with { SourceInstanceId = Guid.NewGuid() };
    var queue = new PlaybackQueue();
    Check(queue.Replace([first], first.Reference));
    Check(!queue.Replace([foreign], first.Reference) && queue.Current?.Reference == first.Reference);
    Check(queue.Select(foreign.Reference) is null && !queue.Remove(foreign.Reference));
    Check(queue.Current?.Reference == first.Reference && queue.Count == 1);
    return Task.CompletedTask;
});
await Test("playback router selects exact sources and never falls back for missing or unsupported sources", async () =>
{
    var router = new MusicPlaybackRouter();
    Guid first = Guid.NewGuid(), second = Guid.NewGuid(), unsupported = Guid.NewGuid();
    int calls = 0;
    router.Register(new FakePlaybackSource(first, MusicSourceCapabilities.Playback, (track, _) =>
    { calls++; Check(track.SourceInstanceId == first); return Task.FromResult<Stream>(new MemoryStream([1])); }));
    router.Register(new FakePlaybackSource(second, MusicSourceCapabilities.Playback, (track, _) =>
    { calls++; Check(track.SourceInstanceId == second); return Task.FromResult<Stream>(new MemoryStream([2])); }));
    router.Register(new FakePlaybackSource(unsupported, MusicSourceCapabilities.Browse, (_, _) => throw new Exception("Unsupported source called")));
    using var one = await router.OpenAsync(new(first, "same-id"), default);
    using var two = await router.OpenAsync(new(second, "same-id"), default);
    Check(one.ReadByte() == 1 && two.ReadByte() == 2 && calls == 2);
    await Fails(MusicFailure.Unavailable, () => router.OpenAsync(new(Guid.NewGuid(), "same-id"), default));
    await Fails(MusicFailure.Rejected, () => router.OpenAsync(new(unsupported, "same-id"), default));
    Check(!router.CanPlay(new(unsupported, "same-id")) && calls == 2);
    router.Unregister(first);
    Check(!router.CanPlay(new(first, "same-id")) && router.CanPlay(new(second, "same-id")));
    router.Unregister(second); router.Unregister(unsupported);
});
await Test("playback router rejects duplicate and empty source registrations", () =>
{
    var router = new MusicPlaybackRouter();
    var provider = new FakePlaybackSource(Guid.NewGuid(), MusicSourceCapabilities.Playback, (_, _) => Task.FromResult<Stream>(new MemoryStream()));
    router.Register(provider);
    try { router.Register(provider); throw new Exception("Duplicate accepted"); } catch (InvalidOperationException) { }
    try { router.Register(new FakePlaybackSource(Guid.Empty, MusicSourceCapabilities.Playback, (_, _) => throw new Exception())); throw new Exception("Empty accepted"); }
    catch (ArgumentException) { }
    router.Unregister(provider.Source.Id);
    return Task.CompletedTask;
});
await Test("source capabilities stay isolated and follow the current registration", () =>
{
    var router = new MusicPlaybackRouter();
    Guid reader = Guid.NewGuid(), writer = Guid.NewGuid();
    router.Register(new FakePlaybackSource(reader, MusicSourceCapabilities.Playback | MusicSourceCapabilities.Lyrics,
        (_, _) => throw new Exception("Capability inspection must not open streams")));
    router.Register(new FakePlaybackSource(writer, MusicSourceCapabilities.Playback | MusicSourceCapabilities.WriteFavorites,
        (_, _) => throw new Exception("Capability inspection must not open streams")));
    Check(router.Supports(reader, MusicSourceCapabilities.Playback | MusicSourceCapabilities.Lyrics));
    Check(!router.Supports(reader, MusicSourceCapabilities.Lyrics | MusicSourceCapabilities.Artwork));
    Check(!router.Supports(reader, MusicSourceCapabilities.WriteFavorites) && router.Supports(writer, MusicSourceCapabilities.WriteFavorites));
    Check(!router.Supports(Guid.Empty, MusicSourceCapabilities.Playback) && !router.Supports(Guid.NewGuid(), MusicSourceCapabilities.Playback));
    Check(!router.Supports(reader, MusicSourceCapabilities.None));
    router.Unregister(writer);
    Check(!router.Supports(writer, MusicSourceCapabilities.WriteFavorites));
    router.Register(new FakePlaybackSource(writer, MusicSourceCapabilities.Playback, (_, _) => throw new Exception()));
    Check(!router.Supports(writer, MusicSourceCapabilities.WriteFavorites) && router.CanPlay(new(writer, "same")));
    router.Unregister(reader); router.Unregister(writer);
    return Task.CompletedTask;
});
await Test("NAS source declares implemented capabilities without sending requests", () =>
{
    using var api = new NasApiClient(ServerEndpoint.Parse("https://example.invalid"),
        new FakeHandler(_ => throw new Exception("Declaration must not contact the service")), Guid.NewGuid());
    var router = new MusicPlaybackRouter(); var source = new NasPlaybackSource(api); router.Register(source);
    Check(router.Supports(api.SourceInstanceId, MusicSourceCapabilities.Browse | MusicSourceCapabilities.Search |
        MusicSourceCapabilities.ReadFavorites | MusicSourceCapabilities.WriteFavorites |
        MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists |
        MusicSourceCapabilities.ReadSettings | MusicSourceCapabilities.WriteSettings |
        MusicSourceCapabilities.Artwork | MusicSourceCapabilities.Lyrics));
    router.Unregister(api.SourceInstanceId);
    return Task.CompletedTask;
});
await Test("playback router cancels disconnected sources and disposes late streams after re-registration", async () =>
{
    var router = new MusicPlaybackRouter(); Guid id = Guid.NewGuid();
    var completion = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationToken requestToken = default;
    router.Register(new FakePlaybackSource(id, MusicSourceCapabilities.Playback, (_, ct) => { requestToken = ct; return completion.Task; }));
    var pending = router.OpenAsync(new(id, "track"), default);
    router.Unregister(id);
    Check(requestToken.IsCancellationRequested);
    router.Register(new FakePlaybackSource(id, MusicSourceCapabilities.Playback, (_, _) => Task.FromResult<Stream>(new MemoryStream([3]))));
    var late = new MemoryStream([1]); completion.SetResult(late);
    await Fails(MusicFailure.Unavailable, () => pending);
    Check(!late.CanRead);
    using var current = await router.OpenAsync(new(id, "track"), default);
    Check(current.ReadByte() == 3);
    router.Unregister(id);
});
await Test("playback router isolates late unauthorized failures from replacement registrations", async () =>
{
    var router = new MusicPlaybackRouter(); Guid id = Guid.NewGuid();
    var completion = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
    router.Register(new FakePlaybackSource(id, MusicSourceCapabilities.Playback, (_, _) => completion.Task));
    var pending = router.OpenAsync(new(id, "track"), default);
    router.Unregister(id);
    router.Register(new FakePlaybackSource(id, MusicSourceCapabilities.Playback, (_, _) => Task.FromException<Stream>(new MusicApiException(MusicFailure.Unauthorized))));
    completion.SetException(new MusicApiException(MusicFailure.Unauthorized));
    await Fails(MusicFailure.Unavailable, () => pending);
    await Fails(MusicFailure.Unauthorized, () => router.OpenAsync(new(id, "track"), default));
    router.Unregister(id);
});
await Test("playback router preserves caller cancellation and disposes a provider stream that ignored it", async () =>
{
    var router = new MusicPlaybackRouter(); Guid id = Guid.NewGuid();
    var completion = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
    router.Register(new FakePlaybackSource(id, MusicSourceCapabilities.Playback, (_, _) => completion.Task));
    using var canceled = new CancellationTokenSource();
    var pending = router.OpenAsync(new(id, "track"), canceled.Token);
    canceled.Cancel();
    var late = new MemoryStream([1]); completion.SetResult(late);
    try { await pending; throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    Check(!late.CanRead);
    router.Unregister(id);
});
Console.WriteLine($"Native checks: {passed} passed.");
return;

static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
}
sealed class PausingPcmStream : MemoryStream
{
    private bool sent;
    public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (!sent) { sent = true; new byte[] { 1, 2, 3, 4 }.CopyTo(buffer); return 4; }
        Paused.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return 0;
    }
}
sealed class AsyncFakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
}
sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
sealed class FakePlaybackSource(Guid id, MusicSourceCapabilities capabilities, Func<TrackReference, CancellationToken, Task<Stream>> open) : IMusicPlaybackSource
{
    public MusicSource Source { get; } = new(id, "test", "synthetic", capabilities);
    public Task<Stream> OpenAsync(TrackReference track, CancellationToken ct) => open(track, ct);
}
static class UriExtensions
{
    public static string Origin(this Uri uri) => uri.GetLeftPart(UriPartial.Authority) + "/";
}
