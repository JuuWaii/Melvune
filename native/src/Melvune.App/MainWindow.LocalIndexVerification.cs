#if DEBUG
using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Windows.Media.Playback;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private async Task VerifyLocalIndexWindowAsync()
    {
        var checks = new Dictionary<string, bool>(); string stage = "setup", error = "";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        void Verify(string name, bool value) { stage = name; checks[name] = value; if (!value) throw new InvalidOperationException(); }
        async Task Until(Func<bool> predicate)
        { while (!predicate()) await Task.Delay(30, deadline.Token); }
        try
        {
            motionPreferenceOverride = false; music.Player.IsMuted = true;
            if (Environment.GetCommandLineArgs().Contains("--seed-local-cache"))
            {
                string root = Path.Combine(AppContext.BaseDirectory, "local-index-fixture"); Directory.CreateDirectory(root);
                foreach (string name in new[] { "first.wav", "second.wav" })
                {
                    using var wave = Services.PlaybackVerification.CreateWave();
                    using var file = new FileStream(Path.Combine(root, name), FileMode.CreateNew); await wave.CopyToAsync(file);
                }
                var saved = await localLibraryStore.LoadAsync();
                var library = new LocalLibrary(Guid.NewGuid(), root, "启动缓存验收", "索引");
                ApplyLocalCatalog(await localLibraryStore.SaveAsync(saved with { Libraries = [library] }));
                var source = await ScanLocalAsync(library.Settings, deadline.Token); ReplaceLocalSource(source);
                await SaveLocalIndexAsync(LocalMusicSource.FromIndex(source.CreateIndex() with { MetadataRevision = 0 }));
                var cached = await ReadLocalIndexAsync(library.Settings, deadline.Token);
                Verify("legacy_metadata_revision_keeps_cached_rows_available", cached?.CreateIndex().MetadataRevision == 0 && cached.Tracks.Count == 2);
                Verify("initial_scan_persisted_two_playable_references", cached?.Tracks.Count == 2 && cached.Tracks.Select(track => track.Reference).SequenceEqual(source.Tracks.Select(track => track.Reference)));
                using var stream = await cached!.OpenAsync(cached.Tracks[0].Reference, deadline.Token);
                Verify("initial_cache_opens_actual_wave_stream", stream.Length > 10000);
                stage = "seed_complete";
            }
            else
            {
                var catalog = await localLibraryStore.LoadAsync(deadline.Token);
                var library = catalog.Libraries.Single(); string root = library.Directory;
                if (root != Path.Combine(AppContext.BaseDirectory, "local-index-fixture")) throw new InvalidDataException();
                string first = Path.Combine(root, "first.wav");
                var before = await ReadLocalIndexAsync(library.Settings, deadline.Token) ?? throw new InvalidOperationException();
                var firstTrack = before.Tracks.Single(track => track.Title == "first");
                // Own fixtures: changed timestamp, deleted second track, new third track.
                File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(1)); File.Delete(Path.Combine(root, "second.wav"));
                using (var wave = Services.PlaybackVerification.CreateWave())
                using (var file = new FileStream(Path.Combine(root, "third.wav"), FileMode.CreateNew)) await wave.CopyToAsync(file);
                int reads = 0; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                localPageMetadataReader = async (file, ct) =>
                {
                    Interlocked.Increment(ref reads); entered.TrySetResult(); await release.Task.WaitAsync(ct);
                    return new(Path.GetFileNameWithoutExtension(file) + " updated", "后台读取", 60);
                };
                await RestoreLocalIndexesAtStartupAsync();
                Verify("new_process_restores_index_before_metadata_scan", reads == 0 && localSources[library.SourceId].Tracks.Count == 2);
                allMusicView = true; localView = false; browseScope = BrowseScope.Local; LibraryPanel.Visibility = Visibility.Visible;
                await LoadAllMusicPageAsync(1);
                Verify("cached_rows_render_without_foreground_scan", reads == 0 && total == 2 && ((IEnumerable<MusicTrack>)TrackList.ItemsSource).Any(track => track.Reference == firstTrack.Reference));
                TrackList.SelectedItem = ((IEnumerable<MusicTrack>)TrackList.ItemsSource).Single(track => track.Reference == firstTrack.Reference);
                await entered.Task.WaitAsync(deadline.Token);
                await StartQueueAsync(firstTrack);
                await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && music.Player.PlaybackSession.Position.TotalSeconds > 1);
                Verify("actual_cached_playback_while_background_metadata_blocked", !release.Task.IsCompleted && localIndexWork is not null
                    && queue.Current?.Reference == firstTrack.Reference && music.Player.Source is not null);
                double position = music.Player.PlaybackSession.Position.TotalSeconds;
                var playingSource = localSources[library.SourceId];
                release.TrySetResult(); await localIndexTask!.WaitAsync(deadline.Token);
                Verify("background_reconciles_added_deleted_changed_rows", reads == 2 && total == 2
                    && localSources[library.SourceId].Tracks.Any(track => track.Title == "third updated")
                    && localSources[library.SourceId].Tracks.Any(track => track.Title == "first updated" && track.Reference == firstTrack.Reference)
                    && localSources[library.SourceId].Tracks.All(track => track.Title != "second"));
                Verify("background_preserves_selection_queue_and_playback", (TrackList.SelectedItem as MusicTrack)?.Reference == firstTrack.Reference
                    && ReferenceEquals(playingSource, localSources[library.SourceId])
                    && queue.Current?.Reference == firstTrack.Reference && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                    && music.Player.PlaybackSession.Position.TotalSeconds >= position);
                var persisted = await ReadLocalIndexAsync(library.Settings, deadline.Token);
                Verify("background_persists_complete_updated_snapshot", persisted?.Tracks.Select(track => track.Title).SequenceEqual(localSources[library.SourceId].Tracks.Select(track => track.Title)) == true);
                localPageMetadataReader = (_, _) => throw new IOException();
                Stop_Click(this, new RoutedEventArgs());
                await music.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5));
                Verify("stop_releases_actual_cached_media", music.Player.Source is null);
                // Metadata delegate is never called for an unchanged persisted snapshot.
                var unchanged = await LocalMusicSource.ScanAsync(library.Settings, deadline.Token, localPageMetadataReader, previous: persisted);
                Verify("persisted_file_stamps_skip_unchanged_metadata", unchanged.Tracks.Select(track => track.Title).SequenceEqual(persisted!.Tracks.Select(track => track.Title)));
                var retained = localSources[library.SourceId];
                string cacheFile = Path.Combine(ThemeSettingsDirectory(), "local-index", library.SourceId.ToString("N") + ".json");
                byte[] committed = await File.ReadAllBytesAsync(cacheFile);
                File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(2));
                var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                localPageMetadataReader = async (_, ct) =>
                { cancelEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new("never", "never", 60); };
                RequestLocalIndexRefresh(library.SourceId); await cancelEntered.Task.WaitAsync(deadline.Token);
                CancelLocalScan_Click(this, new RoutedEventArgs()); await localIndexTask!.WaitAsync(deadline.Token);
                byte[] afterCancel = await File.ReadAllBytesAsync(cacheFile);
                Verify("cancel_background_retains_registered_source_and_complete_disk_index", ReferenceEquals(retained, localSources[library.SourceId]) && committed.SequenceEqual(afterCancel));
                var staleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var staleRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                localPageMetadataReader = async (_, ct) =>
                { staleEntered.TrySetResult(); await staleRelease.Task.WaitAsync(ct); return new("obsolete", "obsolete", 60); };
                RequestLocalIndexRefresh(library.SourceId); await staleEntered.Task.WaitAsync(deadline.Token);
                string replacement = Path.Combine(AppContext.BaseDirectory, "local-index-relinked-fixture"); Directory.CreateDirectory(replacement);
                try
                {
                    ApplyLocalCatalog(await localLibraryStore.SaveAsync(localCatalog with { Libraries = [library with { Directory = replacement }] }, deadline.Token));
                    var relinked = await LocalMusicSource.ScanAsync(localCatalog.Libraries.Single().Settings, deadline.Token);
                    ReplaceLocalSource(relinked); await SaveLocalIndexAsync(relinked);
                }
                finally { staleRelease.TrySetResult(); }
                await localIndexTask!.WaitAsync(deadline.Token);
                Verify("late_scan_cannot_replace_relinked_library_or_its_index", localSources[library.SourceId].Tracks.Count == 0
                    && await ReadLocalIndexAsync(library.Settings, deadline.Token) is null
                    && (await ReadLocalIndexAsync(localCatalog.Libraries.Single().Settings, deadline.Token))?.Tracks.Count == 0);
                stage = "complete";
            }
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally { release.TrySetResult(); localPageMetadataReader = null; motionPreferenceOverride = null; }
        string report = Environment.GetCommandLineArgs().Contains("--seed-local-cache") ? "local-cache-seed.json" : "local-cache-window-verification.json";
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, report), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks }));
    }
}
#endif
