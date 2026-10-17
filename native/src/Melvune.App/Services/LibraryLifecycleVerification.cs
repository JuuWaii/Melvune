#if DEBUG
using System.Text.Json;
using Melvune.App.ViewModels;
using Melvune.Core;
using Melvune.Infrastructure;

namespace Melvune.App.Services;

// Explicit, opt-in NAS write probe. The caller supplies two copied directories
// beneath one unique fixture root. Existing libraries and original files are
// never edited. Reports contain checks/counters, never session material.
internal static partial class LibraryLifecycleVerification
{
    private sealed record FixtureFile(string Name, string Sha256);
    private sealed record Fixture(string OriginalPath, string ReplacementPath, int FileCount, IReadOnlyList<FixtureFile> Files);
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("Library fixture invariant failed."); }
    private static string ValidateFixture(Fixture fixture)
    {
        string suffix = "/original";
        Require(fixture.OriginalPath.StartsWith('/') && fixture.OriginalPath.EndsWith(suffix, StringComparison.Ordinal));
        string root = fixture.OriginalPath[..^suffix.Length];
        string leaf = root[(root.LastIndexOf('/') + 1)..];
        Require(leaf.StartsWith("melvune-verify-", StringComparison.Ordinal) && Guid.TryParseExact(leaf[15..], "N", out _));
        Require(root[1..].Split('/').All(segment => !string.IsNullOrWhiteSpace(segment) && segment is not ("." or "..") && !segment.Contains('\\') && !segment.Any(char.IsControl)));
        Require(fixture.ReplacementPath == root + "/replacement" && fixture.FileCount is >= 1 and <= 20);
        Require(fixture.Files is not null && fixture.Files.Count == fixture.FileCount);
        var files = fixture.Files ?? throw new InvalidOperationException();
        Require(files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() == files.Count);
        Require(files.All(file => !string.IsNullOrWhiteSpace(file.Name) && file.Name.Length <= 256 && file.Name is not ("." or "..")
            && !file.Name.Contains('/') && !file.Name.Contains('\\') && !file.Name.Any(char.IsControl)
            && file.Sha256.Length == 64 && file.Sha256.All(Uri.IsHexDigit)));
        return root;
    }
    private static bool Within(string path, string directory) => path == directory || path.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal);
    private static bool Owned(NasSharedLibrary library, Fixture fixture, HashSet<string> before) =>
        !before.Contains(library.Library.Id) && (library.Path == fixture.OriginalPath || library.Path == fixture.ReplacementPath)
        && library.MetadataPreference == "local_only";

    public static async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>(); var scans = new List<object>(); var scanObservations = new List<object>();
        string stage = "fixture", error = ""; Fixture? fixture = null;
        NasSharedLibrary? owned = null; bool attempted = false, cleaned = false;
        NasPlaylistSnapshot? ownedPlaylist = null;
        IReadOnlyList<NasPlaylist>? playlistsBefore = null;
        bool playlistAttempted = false;
        string playlistName = "FN验证-" + Guid.NewGuid().ToString("N")[..16];
        var allowedTracks = new HashSet<TrackReference>();
        IReadOnlyList<NasSharedLibrary>? before = null;
        var existingTaskIds = new HashSet<(string, long)>();
        using var connection = PlaybackVerification.CreateConnection(); using var observer = PlaybackVerification.CreateConnection();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var scanLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<(object? Result, Exception? Error)>? reconnectObservation = null;
        void Verify(string name, bool value) { checks[name] = value; Require(value); }
        try
        {
            string config = Environment.GetEnvironmentVariable("MELVUNE_LIBRARY_FIXTURE_CONFIG") ?? throw new InvalidOperationException();
            Require(new FileInfo(config).Length <= 16384);
            fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(config, timeout.Token)) ?? throw new InvalidOperationException();
            string root = ValidateFixture(fixture);
            stage = "session";
            await connection.InitializeAsync(); await observer.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
            var other = observer.AuthenticatedClient ?? throw new InvalidOperationException();
            Verify(stage, api.SourceInstanceId == other.SourceInstanceId && await api.IsAdministratorAsync(timeout.Token));
            stage = "authorized_fixture_scope";
            var directories = await api.ListAuthorizedDirectoriesAsync(timeout.Token);
            Verify(stage, directories.Any(directory => Within(root, directory)));
            before = await api.ListSharedLibrariesAsync(timeout.Token);
            Verify("no_existing_library_overlap", before.All(library => !Within(library.Path, root) && !Within(root, library.Path)));
            var baseline = (await api.ListBackgroundTasksAsync(timeout.Token)).Select(task => (task.Id, task.CreatedAt)).ToHashSet();
            existingTaskIds = baseline;
            stage = "create_and_readback"; attempted = true;
            owned = await api.CreateSharedLibraryAsync(fixture.OriginalPath, false, "local_only", timeout.Token);
            Verify(stage, Owned(owned, fixture, before.Select(item => item.Library.Id).ToHashSet()));
            Verify("second_client_observes_created_library", await other.GetSharedLibraryAsync(owned.Library, timeout.Token) == owned);
            stage = "creation_scan_completed";
            scans.Add(await WaitForScanAsync(api, owned.Library.Id, baseline, fixture.FileCount, stage, scanObservations, timeout.Token)); Verify(stage, true);
            stage = "indexed_files_and_bytes_before_reconnect";
            var indexedBefore = await ReadFixtureFilesAsync(api, fixture, fixture.OriginalPath, timeout.Token);
            await VerifyFixtureBytesAsync(api, indexedBefore, fixture, timeout.Token); Verify(stage, true);
            allowedTracks = indexedBefore.Select(file => file.Track.Reference).ToHashSet();
            playlistsBefore = await api.ListPlaylistsAsync(timeout.Token);
            stage = "fixture_playlist_created"; playlistAttempted = true;
            var playlist = await api.CreatePlaylistAsync(playlistName, timeout.Token);
            Require(playlistsBefore.All(item => item.Reference != playlist.Reference));
            ownedPlaylist = await api.ReadPlaylistSnapshotAsync(playlist.Reference, timeout.Token);
            ownedPlaylist = await api.ChangePlaylistTracksAsync(ownedPlaylist, allowedTracks.ToArray(), true, timeout.Token);
            Verify(stage, ownedPlaylist.Tracks.Count == fixture.FileCount && allowedTracks.SetEquals(ownedPlaylist.Tracks.Select(track => track.Reference)));
            var remoteReferences = ownedPlaylist.Tracks.Select(track => track.Reference).ToArray();
            var storePath = Path.Combine(AppContext.BaseDirectory, "library-reference-store", Guid.NewGuid().ToString("N"));
            var store = new LocalPlaylistStore(storePath);
            var localPlaylist = new LocalPlaylist(Guid.NewGuid(), playlistName, indexedBefore.Select(file => LocalPlaylistEntry.FromTrack(file.Track, owned.Library.Id)).ToArray());
            await store.SaveAsync(new(0, [localPlaylist]), timeout.Token);
            Verify("local_playlist_saved_before_reconnect", (await store.LoadAsync(timeout.Token)).Playlists.Single().Entries.SequenceEqual(localPlaylist.Entries));

            stage = "option_change_readback";
            var stale = owned;
            owned = await api.UpdateSharedLibraryAsync(owned, true, "local_only", timeout.Token);
            Verify(stage, owned.AutoDownloadLyric && await other.GetSharedLibraryAsync(owned.Library, timeout.Token) == owned);
            stage = "stale_option_write_rejected";
            bool rejected = false;
            try { await other.UpdateSharedLibraryAsync(stale, false, "local_only", timeout.Token); }
            catch (RemoteSettingsConflictException) { rejected = true; }
            Verify(stage, rejected);
            owned = await api.UpdateSharedLibraryAsync(owned, false, "local_only", timeout.Token);
            Verify("fixture_options_restored", !owned.AutoDownloadLyric);

            stage = "replacement_preflight_matches";
            var preflight = await api.CheckLibraryReconnectAsync(owned, fixture.ReplacementPath, timeout.Token);
            // Do not silently confirm unmatched copies or server confirmation requirements.
            Verify(stage, preflight.MatchAccepted && !preflight.NeedsConfirmation);
            baseline = (await api.ListBackgroundTasksAsync(timeout.Token)).Select(task => (task.Id, task.CreatedAt)).ToHashSet();
            stage = "reconnect_and_readback";
            var result = await api.ReconnectLibraryAsync(preflight, false, timeout.Token);
            Verify(stage, result.Library.Library.Id == owned.Library.Id && result.Library.Path == fixture.ReplacementPath
                && result.Library.MetadataPreference == owned.MetadataPreference && result.Library.AutoDownloadLyric == owned.AutoDownloadLyric);
            owned = result.Library;
            Verify("second_client_observes_reconnected_library", await other.GetSharedLibraryAsync(owned.Library, timeout.Token) == owned);
            stage = "reconnect_scan_completed";
            if (!result.ScanStarted) await api.ScanSharedLibraryAsync(owned.Library, timeout.Token);
            reconnectObservation = ObserveScanAsync(api, owned.Library.Id, baseline, fixture.FileCount, stage, scanObservations, scanLifetime.Token);
            stage = "indexed_identity_and_bytes_after_reconnect";
            var indexedAfter = await WaitForFixtureFilesAsync(api, fixture, fixture.ReplacementPath, timeout.Token);
            Verify(stage, indexedBefore.Select(file => (file.Track.Reference, Name(file.Path)))
                .SequenceEqual(indexedAfter.Select(file => (file.Track.Reference, Name(file.Path)))));
            await VerifyFixtureBytesAsync(api, indexedAfter, fixture, timeout.Token);
            Verify("encoded_audio_hashes_preserved", true);
            stage = "remote_playlist_references_preserved";
            var observedPlaylist = await other.ReadPlaylistSnapshotAsync(ownedPlaylist.Playlist.Reference, timeout.Token);
            Verify(stage, observedPlaylist.Tracks.Select(track => track.Reference).SequenceEqual(remoteReferences));
            stage = "local_playlist_restart_references_preserved";
            var restored = (await new LocalPlaylistStore(storePath).LoadAsync(timeout.Token)).Playlists.Single();
            Verify(stage, restored.Id == localPlaylist.Id && restored.Entries.SequenceEqual(localPlaylist.Entries)
                && restored.Entries.All(entry => indexedAfter.Any(file => file.Track.Reference == entry.Track)));
            stage = "reconnect_scan_completed";
            var observedScan = await reconnectObservation;
            if (observedScan.Error is { } scanError)
            {
                checks[stage] = false;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(scanError).Throw();
            }
            scans.Add(observedScan.Result!); Verify(stage, true);
            // Keep explicit rescan acceptance separate from the reconnect reference checks.
            // A missing completion record still fails the full probe, after collecting
            // the independent evidence rather than preventing those checks from running.
            baseline = (await api.ListBackgroundTasksAsync(timeout.Token)).Select(task => (task.Id, task.CreatedAt)).ToHashSet();
            stage = "explicit_scan_submitted";
            await api.ScanSharedLibraryAsync(owned.Library, timeout.Token); Verify(stage, true);
            stage = "explicit_scan_completed";
            scans.Add(await WaitForScanAsync(api, owned.Library.Id, baseline, fixture.FileCount, stage, scanObservations, timeout.Token)); Verify(stage, true);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally
        {
            scanLifetime.Cancel();
            if (reconnectObservation is not null) await reconnectObservation;
            bool playlistCleaned = !playlistAttempted;
            if (playlistAttempted && playlistsBefore is not null && connection.AuthenticatedClient is { } playlistApi)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var candidates = (await playlistApi.ListPlaylistsAsync(cleanup.Token)).Where(item => item.Name == playlistName
                        && playlistsBefore.All(original => original.Reference != item.Reference)).ToArray();
                    Require(candidates.Length <= 1 && (ownedPlaylist is null || candidates.All(item => item.Reference == ownedPlaylist.Playlist.Reference)));
                    foreach (var candidate in candidates)
                    {
                        var current = await playlistApi.ReadPlaylistSnapshotAsync(candidate.Reference, cleanup.Token);
                        Require(current.Playlist.Name == playlistName && current.Tracks.All(track => allowedTracks.Contains(track.Reference)));
                        await playlistApi.DeletePlaylistAsync(current, cleanup.Token);
                    }
                    var remaining = await playlistApi.ListPlaylistsAsync(cleanup.Token);
                    playlistCleaned = remaining.Count == playlistsBefore.Count && playlistsBefore.All(original => remaining.Contains(original));
                }
                catch (Exception) { }
                checks["fixture_playlist_removed_existing_playlists_unchanged"] = playlistCleaned;
                if (!playlistCleaned) stage = "cleanup_incomplete";
            }
            if (attempted && fixture is not null && before is not null && connection.AuthenticatedClient is { } api)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    Require(playlistCleaned);
                    var beforeIds = before.Select(item => item.Library.Id).ToHashSet();
                    var candidates = (await api.ListSharedLibrariesAsync(cleanup.Token)).Where(item => Owned(item, fixture, beforeIds)).ToArray();
                    // Recover an uncertain create response by its unique path, without
                    // inferring ownership from a name or touching a pre-existing ID.
                    Require(candidates.Length <= 1 && (owned is null || candidates.All(item => item.Library.Id == owned.Library.Id)));
                    foreach (var candidate in candidates)
                    {
                        var active = (await api.ListBackgroundTasksAsync(cleanup.Token)).Where(task => task.LibraryId == candidate.Library.Id
                            && !existingTaskIds.Contains((task.Id, task.CreatedAt)) && !task.Done).ToArray();
                        foreach (var task in active.Where(task => task.CanCancel)) await api.ChangeBackgroundTaskAsync(task, NasTaskAction.Cancel, cleanup.Token);
                        while ((await api.ListBackgroundTasksAsync(cleanup.Token)).Any(task => task.LibraryId == candidate.Library.Id && !task.Done))
                            await Task.Delay(TimeSpan.FromSeconds(1), cleanup.Token);
                        await api.DeleteSharedLibraryAsync(candidate, cleanup.Token);
                    }
                    var remaining = await api.ListSharedLibrariesAsync(cleanup.Token);
                    cleaned = remaining.Count == before.Count && before.All(original => remaining.Any(item => item == original));
                }
                catch (Exception) { }
                checks["fixture_library_removed_existing_libraries_unchanged"] = cleaned;
                if (!cleaned) stage = "cleanup_incomplete";
            }
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "library-lifecycle-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, scans, scanObservations }));
    }

    private static async Task<(object? Result, Exception? Error)> ObserveScanAsync(NasApiClient api, string libraryId,
        HashSet<(string, long)> baseline, int files, string stage, List<object> observations, CancellationToken ct)
    {
        try { return (await WaitForScanAsync(api, libraryId, baseline, files, stage, observations, ct).ConfigureAwait(false), null); }
        catch (Exception error) { return (null, error); }
    }

    private static async Task<object> WaitForScanAsync(NasApiClient api, string libraryId,
        HashSet<(string, long)> baseline, int files, string stage, List<object> observations, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromMinutes(2));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        string? previousState = null;
        int polls = 0;
        while (true)
        {
            long requestedAt = elapsed.ElapsedMilliseconds;
            // Reference hashing and XAML callbacks must not delay this observer's
            // continuation. Request latency is recorded separately from poll delay.
            var all = await api.ListBackgroundTasksAsync(bounded.Token).ConfigureAwait(false);
            long returnedAt = elapsed.ElapsedMilliseconds;
            polls++;
            var associated = all.Where(task => task.LibraryId == libraryId && task.Type == "fileScan").ToArray();
            var states = associated.Select(task => new { task.Id, task.CreatedAt, IsBaseline = baseline.Contains((task.Id, task.CreatedAt)),
                task.Total, task.SuccessCount, task.FailCount, task.CanceledCount, task.Done, task.Cancelling, task.Canceled }).ToArray();
            string state = JsonSerializer.Serialize(new { TotalTasks = all.Count, Associated = states });
            if (state != previousState && observations.Count < 150)
                observations.Add(new { Stage = stage, ElapsedMilliseconds = returnedAt, Poll = polls,
                    RequestMilliseconds = returnedAt - requestedAt, TotalTasks = all.Count, Associated = states });
            previousState = state;
            var tasks = associated.Where(task => !baseline.Contains((task.Id, task.CreatedAt))).ToArray();
            Require(tasks.Length <= 1);
            if (tasks.SingleOrDefault() is { Done: true } task)
            {
                Require(!task.Cancelling && !task.Canceled && task.FailCount == 0 && task.CanceledCount == 0 && task.Total == files && task.SuccessCount == files);
                return new { task.Type, task.Total, task.SuccessCount, task.FailCount, task.CanceledCount, task.Done };
            }
            // Bound the initial observation burst; do not turn a vanished task
            // into success or keep high-frequency requests running indefinitely.
            await Task.Delay(elapsed.Elapsed < TimeSpan.FromSeconds(10) ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(2), bounded.Token).ConfigureAwait(false);
        }
    }
}
#endif
