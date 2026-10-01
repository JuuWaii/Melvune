#if DEBUG
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private async Task VerifyMultiLibraryWindowAsync(Action<string, bool> verify)
    {
        var libraries = new List<LocalLibrary>();
        for (int i = 0; i < 2; i++)
        {
            string root = Path.Combine(AppContext.BaseDirectory, "multi-library-fixture", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using (var wave = Services.PlaybackVerification.CreateWave())
            using (var file = File.Create(Path.Combine(root, "same.wav"))) await wave.CopyToAsync(file);
            await File.WriteAllTextAsync(Path.Combine(root, "same.lrc"), "[00:00]音乐库" + i);
            libraries.Add(new(Guid.NewGuid(), root, "音乐库" + i, "分组" + i));
        }
        var saved = await localLibraryStore.LoadAsync();
        saved = await localLibraryStore.SaveAsync(saved with { Libraries = libraries });
        ApplyLocalCatalog(saved);
        selectedLocalLibrary = null; selectedLocalGroup = null;
        await ShowLocalMusicAsync();
        var tracks = ((IEnumerable<MusicTrack>)TrackList.ItemsSource).ToArray();
        verify("multiple_libraries_same_id_isolated", total == 2 && tracks[0].Id == tracks[1].Id && tracks[0].Reference != tracks[1].Reference);
        verify("multiple_library_source_labels", tracks.All(track => !string.IsNullOrWhiteSpace(track.SourceLabel)));
        LocalGroupFilter.SelectedIndex = 2;
        for (int i = 0; i < 60 && total != 1; i++) await Task.Delay(50);
        verify("library_group_filter", total == 1 && ((MusicTrack)TrackList.SelectedItem).SourceInstanceId == libraries[1].SourceId);
        LocalGroupFilter.SelectedIndex = 0;
        for (int i = 0; i < 60 && total != 2; i++) await Task.Delay(50);
        verify("library_all_groups_restored", total == 2);
        var entries = tracks.Select(track => LocalPlaylistEntry.FromTrack(track)).ToArray();
        verify("multi_library_scanned_membership", tracks.All(track => track.LibraryIds.SequenceEqual(new[] { track.SourceInstanceId.ToString("N") })));
        var playlist = new LocalPlaylist(Guid.NewGuid(), "跨库窗口夹具", entries);
        var catalog = await playlistStore.LoadAsync();
        await playlistStore.SaveAsync(catalog with { Playlists = [playlist] });
        await PlayLocalPlaylistAsync(playlist, entries[0]);
        for (int i = 0; i < 80 && !mediaReady; i++) await Task.Delay(100);
        var audio = music.Player.Source;
        var remote = new MusicTrack("remote", "断开的飞牛条目", "夹具", 1, false) { SourceInstanceId = Guid.NewGuid() };
        queue.Replace([tracks[0], remote, tracks[1]], tracks[0].Reference);
        ResetLibrary();
        verify("nas_reset_preserves_multi_library_queue", queue.Count == 2 && mediaReady && ReferenceEquals(audio, music.Player.Source)
            && queue.Tracks.All(track => localSources.ContainsKey(track.SourceInstanceId)));
        await PlayTrackAsync(queue.Next(false) ?? throw new InvalidOperationException());
        for (int i = 0; i < 80 && !mediaReady; i++) await Task.Delay(100);
        verify("cross_library_next_lyrics", mediaReady && currentLyrics.Lines.Count == 1 && currentLyrics.Lines[0].Text == "音乐库1");
        var beforeRefreshSources = localSources.ToDictionary(item => item.Key, item => item.Value);
        var beforeRefreshAudio = music.Player.Source;
        var secondScanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int scannedFiles = 0;
        localPageMetadataReader = async (_, ct) =>
        {
            if (++scannedFiles == 2)
            {
                secondScanEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new("刷新夹具", "夹具", 60);
        };
        try
        {
            var refresh = LoadLocalPageAsync(1, true);
            await secondScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            verify("multi_library_refresh_not_partially_committed", beforeRefreshSources.All(item => ReferenceEquals(localSources[item.Key], item.Value)));
            CancelLocalScan_Click(this, new RoutedEventArgs()); await refresh;
            verify("multi_library_cancel_keeps_sources_and_playback", beforeRefreshSources.All(item => ReferenceEquals(localSources[item.Key], item.Value))
                && mediaReady && ReferenceEquals(beforeRefreshAudio, music.Player.Source) && entries.All(PlaylistTrackAvailable));

            localPageMetadataReader = (_, _) => Task.FromResult(new LocalMusicMetadata("刷新成功", "夹具", 60));
            await LoadLocalPageAsync(1, true);
            verify("multi_library_refresh_retry_commits_all", beforeRefreshSources.All(item => !ReferenceEquals(localSources[item.Key], item.Value))
                && ((IEnumerable<MusicTrack>)TrackList.ItemsSource).All(track => track.Title == "刷新成功") && total == 2);
            verify("multi_library_refresh_keeps_identity_playlist_playback", entries.All(PlaylistTrackAvailable)
                && entries.All(entry => localSources[entry.Track.SourceInstanceId].Tracks.Any(track => track.Reference == entry.Track))
                && mediaReady && ReferenceEquals(beforeRefreshAudio, music.Player.Source));

            scannedFiles = 0;
            secondScanEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            localPageMetadataReader = async (_, ct) =>
            {
                if (++scannedFiles == 2)
                { secondScanEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
                return new("刷新夹具", "夹具", 60);
            };
            var staleRefresh = LoadLocalPageAsync(1, true);
            await secondScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await SwitchCollectionAsync(null);
            var remoteItems = TrackList.ItemsSource;
            await staleRefresh;
            verify("local_refresh_navigation_does_not_restore_old_list", !localView && ReferenceEquals(TrackList.ItemsSource, remoteItems)
                && TrackList.SelectedItem is MusicTrack { Id: "synthetic-a" });
        }
        finally { localPageMetadataReader = null; }
        Stop_Click(this, new RoutedEventArgs());
        queue.Replace([remote, tracks[0]], remote.Reference);
        ResetLibrary();
        verify("nas_reset_stops_remote_keeps_local_entries", queue.Count == 1 && queue.Current is null && !mediaReady
            && queue.Tracks[0].Reference == tracks[0].Reference);
        // Recreate registrations from disk, as after restart. Stable IDs keep playlist references usable.
        foreach (var id in localSources.Keys.ToArray()) ViewModel.PlaybackRouter.Unregister(id);
        localSources.Clear(); localCatalogLoaded = false;
        await RestoreLocalSourcesAsync(default);
        var reloaded = (await playlistStore.LoadAsync()).Playlists.Single();
        verify("multi_library_playlist_restart_restore", reloaded.Entries.All(PlaylistTrackAvailable) && localSources.Count == 2);
        verify("multi_library_membership_restart_restore", reloaded.Entries.All(entry => entry.ToTrack().GetLibraries().Single()
            == new LibraryReference(entry.Track.SourceInstanceId, entry.Track.SourceInstanceId.ToString("N"))));
        var unavailable = libraries[1] with { Directory = Path.Combine(libraries[1].Directory, "missing") };
        ApplyLocalCatalog(saved with { Libraries = [libraries[0], unavailable] });
        await RestoreLocalSourcesAsync(default);
        verify("missing_library_keeps_playlist_reference", !PlaylistTrackAvailable(entries[1]) && (await playlistStore.LoadAsync()).Playlists.Single().Entries.Count == 2);
        ApplyLocalCatalog(saved);
        await RestoreLocalSourcesAsync(default);
        verify("library_reassociated_same_identity", PlaylistTrackAvailable(entries[1]));
        ApplyLocalCatalog(saved with { Libraries = [libraries[0]] });
        verify("library_remove_preserves_other_source", localSources.Count == 1 && PlaylistTrackAvailable(entries[0]) && !PlaylistTrackAvailable(entries[1]) && (await playlistStore.LoadAsync()).Playlists.Single().Entries.Count == 2);
    }
}
#endif
