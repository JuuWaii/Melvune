#if DEBUG
using System.Text.Json;
using FnMusic.Core;
using Microsoft.UI.Xaml;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    // 窗口内状态回归：调用生产加载/导航方法并断言真实控件，不模拟鼠标事件。
    private async Task VerifyCollectionWindowAsync()
    {
        var checks = new Dictionary<string, bool>();
        var timings = new Dictionary<string, long>();
        string stage = "setup", error = "";
        void Verify(string name, bool value)
        { stage = name; checks[name] = value; if (!value) throw new InvalidOperationException(); }
        try
        {
            motionPreferenceOverride = false;
            music.Player.IsMuted = true;
            if (!Environment.GetCommandLineArgs().Contains("--preview-collections")) throw new InvalidOperationException();
            SettingsPanel.Visibility = Visibility.Collapsed; LibraryPanel.Visibility = Visibility.Visible;
            await VerifyRemoteCommandsWindowAsync(Verify);
            foreach (var kind in new[] { CollectionKind.Album, CollectionKind.Artist })
            {
                string prefix = kind == CollectionKind.Album ? "album" : "artist";
                await SwitchCollectionAsync(kind);
                Verify(prefix + "_first_page", page == 1 && total == 53 && !PreviousPage.IsEnabled && NextPage.IsEnabled &&
                    CollectionList.ItemsSource is IEnumerable<MusicCollection> firstItems && firstItems.Count() == 50);
                await LoadPageAsync(2);
                var items = (CollectionList.ItemsSource as IEnumerable<MusicCollection> ?? throw new InvalidOperationException()).ToArray();
                Verify(prefix + "_last_page", page == 2 && items.Length == 3 && items[0].Id == "collection-51" && PreviousPage.IsEnabled && !NextPage.IsEnabled);
                await OpenCollectionAsync(items[0]);
                Verify(prefix + "_detail_first_page", page == 1 && total == 53 && TrackList.Visibility == Visibility.Visible && NextPage.IsEnabled);
                await LoadPageAsync(2);
                var tracks = (TrackList.ItemsSource as IEnumerable<MusicTrack> ?? throw new InvalidOperationException()).ToArray();
                Verify(prefix + "_detail_last_page", tracks.Length == 3 && tracks[0].Id.EndsWith("-track-51") && !NextPage.IsEnabled && PreviousPage.IsEnabled);
                if (kind == CollectionKind.Artist)
                {
                    await ShowArtistAlbumsAsync();
                    Verify("artist_albums_list", parentArtist?.Id == "collection-51" && collectionKind == CollectionKind.Album && collection is null && page == 1 && total == 53);
                    await LoadPageAsync(2);
                    var album = ((IEnumerable<MusicCollection>)CollectionList.ItemsSource!).First();
                    await OpenCollectionAsync(album);
                    Verify("artist_album_detail", parentArtist is not null && collection?.Id == album.Id && TrackList.Visibility == Visibility.Visible);
                    await ReturnToCollectionListAsync();
                    Verify("artist_albums_return_page", parentArtist is not null && collection is null && page == 2);
                    await ReturnToCollectionListAsync();
                    Verify("artist_return_track_page", parentArtist is null && collectionKind == CollectionKind.Artist && collection?.Id == "collection-51" && page == 2);
                }
                await ReturnToCollectionListAsync();
                Verify(prefix + "_return_page", page == 2 && CollectionList.Visibility == Visibility.Visible && CollectionOpen.IsEnabled);
                await OpenCollectionAsync(items[1]);
                Verify(prefix + "_empty_detail", total == 0 && !PreviousPage.IsEnabled && !NextPage.IsEnabled &&
                    TrackList.ItemsSource is IEnumerable<MusicTrack> emptyTracks && !emptyTracks.Any() && PageStatus.Text == "当前详情暂无曲目。");
                await ReturnToCollectionListAsync();
                var pending = OpenCollectionAsync(items[2]);
                Verify(prefix + "_loading", TrackList.ItemsSource is null && !PreviousPage.IsEnabled && !NextPage.IsEnabled);
                await SwitchCollectionAsync(kind == CollectionKind.Album ? CollectionKind.Artist : CollectionKind.Album);
                string expectedHeading = CollectionHeading.Text;
                await pending;
                Verify(prefix + "_stale_detail_ignored", collection is null && page == 1 && total == 53 && CollectionHeading.Text == expectedHeading &&
                    CollectionList.Visibility == Visibility.Visible && TrackList.ItemsSource is null && NextPage.IsEnabled);
                syntheticLibraryFailure = (MusicFailure.Unavailable, 0);
                await LoadPageAsync(1);
                Verify(prefix + "_network_error", CollectionList.ItemsSource is null && !CollectionOpen.IsEnabled &&
                    !PreviousPage.IsEnabled && !NextPage.IsEnabled && PageStatus.Text.StartsWith("加载失败"));
                await LoadPageAsync(1);
                Verify(prefix + "_network_retry", total == 53 && CollectionOpen.IsEnabled && NextPage.IsEnabled);
                var queuedTrack = new MusicTrack("synthetic-queued", "合成曲目", "测试夹具", 60, false);
                queue.Replace([queuedTrack], queuedTrack.Reference);
                syntheticLibraryFailure = (MusicFailure.Unauthorized, 100);
                var staleFailure = LoadPageAsync(1);
                await SwitchCollectionAsync(kind);
                await staleFailure;
                Verify(prefix + "_stale_unauthorized_ignored", collectionKind == kind && total == 53 && queue.Count == 1 && CollectionOpen.IsEnabled);
                syntheticLibraryFailure = (MusicFailure.Unauthorized, 0);
                await LoadPageAsync(1);
                Verify(prefix + "_expired_session_reset", collectionKind is null && collection is null && queue.Count == 0 &&
                    CollectionList.ItemsSource is null && TrackList.ItemsSource is null && !NextPage.IsEnabled &&
                    CollectionHeading.Visibility == Visibility.Collapsed && PlaybackStatus.Text.StartsWith("登录已失效"));
            }
            syntheticFavorites.Clear();
            await SwitchCollectionAsync(null);
            Verify("favorite_initial", TrackList.SelectedItem is MusicTrack { IsFavorite: false } && FavoriteToggle.IsEnabled);
            await ToggleFavoriteAsync();
            Verify("favorite_added", TrackList.SelectedItem is MusicTrack { IsFavorite: true } && FavoriteToggle.Content.ToString() == "取消收藏");
            await ShowFavoritesAsync();
            Verify("favorite_list", total == 1 && TrackList.SelectedItem is MusicTrack { Id: "synthetic-a", IsFavorite: true });
            var favoriteTrack = (MusicTrack)TrackList.SelectedItem;
            queue.Replace([favoriteTrack], favoriteTrack.Reference);
            syntheticFavoriteFailure = MusicFailure.Unavailable;
            await ToggleFavoriteAsync();
            Verify("favorite_failed_removal", total == 1 && TrackList.SelectedItem is MusicTrack { IsFavorite: true } && FavoriteToggle.IsEnabled && FavoriteStatus.Text.StartsWith("未能确认"));
            await ToggleFavoriteAsync();
            Verify("favorite_removed", total == 0 && TrackList.SelectedItem is null && !FavoriteToggle.IsEnabled && queue.Count == 1);
            await SwitchCollectionAsync(null);
            Verify("favorite_state_reloaded", TrackList.SelectedItem is MusicTrack { IsFavorite: false });
            var beforeForeignItems = TrackList.ItemsSource;
            var beforeForeignSelection = TrackList.SelectedItem;
            var foreignFavorite = (beforeForeignSelection as MusicTrack ?? throw new InvalidOperationException()) with { SourceInstanceId = Guid.NewGuid() };
            TrackList.ItemsSource = new[] { foreignFavorite }; TrackList.SelectedIndex = 0;
            Verify("favorite_foreign_source_disabled", !FavoriteToggle.IsEnabled);
            await ToggleFavoriteAsync();
            Verify("favorite_foreign_source_write_rejected", syntheticFavorites.Count == 0 && FavoriteStatus.Text.Contains("来源"));
            TrackList.ItemsSource = beforeForeignItems; TrackList.SelectedItem = beforeForeignSelection;
            string localDirectory = Path.Combine(AppContext.BaseDirectory, "local-fixture", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(localDirectory);
            await File.WriteAllTextAsync(Path.Combine(localDirectory, "000-playback.lrc"), "[00:00]第一行\n[00:10]第二行\n[00:20]第三行");
            await File.WriteAllBytesAsync(Path.Combine(localDirectory, "000-playback.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a/noAAAAASUVORK5CYII="));
            using (var wave = Services.PlaybackVerification.CreateWave())
            using (var file = File.Create(Path.Combine(localDirectory, "000-playback.wav"))) await wave.CopyToAsync(file);
            for (int i = 1; i <= 52; i++) await File.WriteAllBytesAsync(Path.Combine(localDirectory, $"track-{i:000}.wav"), [0]);
            var local = await FnMusic.Infrastructure.LocalMusicSource.ScanAsync(new(Guid.NewGuid(), localDirectory), default, Services.LocalMusicMetadataReader.ReadAsync);
            ReplaceLocalSource(local);
            await ShowLocalMusicAsync();
            Verify("local_first_page", total == 53 && page == 1 && NextPage.IsEnabled && !FavoriteToggle.IsEnabled);
            await LoadPageAsync(2);
            Verify("local_last_page", page == 2 && TrackList.ItemsSource is IEnumerable<MusicTrack> localLast && localLast.Count() == 3 && !NextPage.IsEnabled);
            SearchInput.Text = "000-playback"; await SearchAsync();
            Verify("local_search", total == 1 && localView && !FavoriteToggle.IsEnabled);
            Verify("local_duration", TrackList.SelectedItem is MusicTrack durationTrack && Math.Abs(durationTrack.DurationSeconds - 60) < 1);
            await StartQueueAsync(TrackList.SelectedItem as MusicTrack ?? throw new InvalidOperationException());
            for (int i = 0; i < 80 && (!mediaReady || music.Player.PlaybackSession.Position.TotalSeconds <= 0); i++) await Task.Delay(100);
            Verify("local_file_playback", mediaReady && music.Player.PlaybackSession.Position.TotalSeconds > 0 && queue.Current?.SourceInstanceId == local.Source.Id);
            var capabilityTrack = TrackList.SelectedItem as MusicTrack ?? throw new InvalidOperationException();
            ViewModel.PlaybackRouter.Unregister(local.Source.Id);
            ViewModel.PlaybackRouter.Register(new CapabilityPlaybackSource(local, MusicSourceCapabilities.Playback));
            try
            {
                await LoadLyricsAsync(capabilityTrack);
                Verify("unsupported_lyrics_explicit_status", currentLyrics.Lines.Count == 0 && LyricsStatus.Text.Contains("不支持歌词"));
                coverCache.Clear(); await LoadArtworkAsync(NowCover);
                Verify("unsupported_artwork_does_not_load_or_cache", NowCover.Source is null && coverCache.Count == 0 && NowCover.Tag is null);
            }
            finally { ViewModel.PlaybackRouter.Unregister(local.Source.Id); ViewModel.PlaybackRouter.Register(local); }
            await LoadLyricsAsync(capabilityTrack);
            Verify("restored_source_lyrics_capability", currentLyrics.Lines.Count == 3);
            await LoadArtworkAsync(NowCover);
            Verify("local_cover_decoded", NowCover.Source is not null);
            await File.WriteAllBytesAsync(Path.Combine(localDirectory, "000-playback.jpg"), [1, 2, 3]);
            coverCache.Clear(); await LoadArtworkAsync(NowCover);
            Verify("local_corrupt_cover_falls_back", NowCover.Source is not null);
            await File.WriteAllBytesAsync(Path.Combine(localDirectory, "000-playback.jpg"), new byte[4 * 1024 * 1024 + 1]);
            coverCache.Clear(); await LoadArtworkAsync(NowCover);
            Verify("local_oversized_cover_falls_back", NowCover.Source is not null);
            coverCache.Clear();
            var coverEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var coverRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            localArtworkBeforeCommit = () => { coverEntered.TrySetResult(); return coverRelease.Task; };
            var oldCoverRequest = LoadArtworkAsync(NowCover);
            try
            {
                await coverEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var replacement = await FnMusic.Infrastructure.LocalMusicSource.ScanAsync(local.Settings, default);
                ReplaceLocalSource(replacement);
                coverRelease.TrySetResult(); await oldCoverRequest;
                Verify("local_replaced_source_discards_late_cover", NowCover.Source is null && coverCache.Count == 0);
                local = replacement;
            }
            finally { localArtworkBeforeCommit = null; coverRelease.TrySetResult(); }
            await LoadArtworkAsync(NowCover);
            Verify("local_replacement_cover_loads", NowCover.Source is not null);
            Verify("local_lyrics_loaded", currentLyrics.IsTimed && currentLyrics.Lines.Count == 3);
            local = await VerifyLyricsSourceChangesAsync(Verify, local, TrackList.SelectedItem as MusicTrack ?? throw new InvalidOperationException());
            await OpenNowPlayingAsync();
            music.Player.Pause();
            pendingSeek = 20; CommitSeek();
            for (int i = 0; i < 80 && (submittedSeek is not null || Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 20) > 2); i++) await Task.Delay(100);
            Verify("local_seek", music.Player.PlaybackSession.CanSeek && submittedSeek is null && Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 20) < 2);
            UpdateLyricsPosition();
            Verify("lyrics_follow_seek", activeLyric >= 1 && LyricsList.SelectedIndex == activeLyric);
            await VerifyFullscreenPresentationAsync(Verify);
            foreach (int target in new[] { 5, 45, 10 })
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                pendingSeek = target; CommitSeek();
                for (int i = 0; i < 80 && (submittedSeek is not null || Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - target) > 2); i++) await Task.Delay(50);
                timings["repeated_seek_" + target + "_ms"] = timer.ElapsedMilliseconds;
                Verify("repeated_seek_" + target, submittedSeek is null && Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - target) < 2);
            }
            Root.RequestedTheme = ElementTheme.Light;
            Verify("light_theme", Root.ActualTheme == ElementTheme.Light);
            Root.RequestedTheme = ElementTheme.Dark;
            Verify("dark_theme", Root.ActualTheme == ElementTheme.Dark);
            ThemePicker.SelectedIndex = 1;
            SystemThemeChanged(systemTheme, new object());
            await Task.Delay(100);
            Verify("manual_light_survives_system_notification", Root.ActualTheme == ElementTheme.Light && themeSettings.Load() == FnMusic.Infrastructure.ThemePreference.Light);
            ThemePicker.SelectedIndex = 2;
            SystemThemeChanged(systemTheme, new object());
            await Task.Delay(100);
            Verify("manual_dark_survives_system_notification", Root.ActualTheme == ElementTheme.Dark && themeSettings.Load() == FnMusic.Infrastructure.ThemePreference.Dark);
            updatingThemeChoice = true;
            themePreference = FnMusic.Infrastructure.ThemePreference.System;
            InitializeThemeChoice();
            ApplySystemTheme();
            Verify("theme_choice_reloads_saved_mode", ThemePicker.SelectedIndex == 2 && Root.ActualTheme == ElementTheme.Dark);
            ThemePicker.SelectedIndex = 0;
            Verify("system_theme_choice_saved", themeSettings.Load() == FnMusic.Infrastructure.ThemePreference.System);
            ApplySystemTheme();
            var systemRequestedTheme = Root.RequestedTheme;
            Root.RequestedTheme = systemRequestedTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            SystemThemeChanged(systemTheme, new object());
            await Task.Delay(100);
            Verify("system_theme_notification_restores_preference", Root.RequestedTheme == systemRequestedTheme);
            await VerifyDialogThemeAsync(Verify);
            await CloseNowPlayingAsync();
            var originalSize = AppWindow.Size;
            AppWindow.Resize(new global::Windows.Graphics.SizeInt32(980, 780));
            var resized = AppWindow.Size;
            await ShowLibraryAsync();
            Verify("library_navigation_preserves_window_size", AppWindow.Size.Width == resized.Width && AppWindow.Size.Height == resized.Height);
            AppWindow.Resize(originalSize);
            await ShowLocalMusicAsync();
            SearchInput.Text = "000-playback"; await SearchAsync();
            Root.RequestedTheme = ElementTheme.Dark; UpdateTitleBarTheme();
            Verify("dark_titlebar", AppWindow.TitleBar.BackgroundColor?.R == 32 && AppWindow.TitleBar.ButtonForegroundColor?.R == 255);
            Root.RequestedTheme = ElementTheme.Light; UpdateTitleBarTheme();
            Verify("light_titlebar", AppWindow.TitleBar.BackgroundColor?.R == 243 && AppWindow.TitleBar.ButtonForegroundColor?.R == 0);
            ApplySystemTheme();
            await OpenNowPlayingAsync();
            UpdateAdaptiveLayout(800);
            Verify("compact_player_rows", Microsoft.UI.Xaml.Controls.Grid.GetRow(TransportPanel) == 1 && Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(TransportPanel) == 3);
            Verify("compact_lyrics_readable_column", Microsoft.UI.Xaml.Controls.Grid.GetColumn(FullLyricsText) == 0
                && Microsoft.UI.Xaml.Controls.Grid.GetColumnSpan(FullLyricsText) == 2 && FullArtworkPanel.Visibility == Visibility.Collapsed);
            UpdateCollectionView();
            Verify("browse_refresh_keeps_fullscreen", LyricsPanel.Visibility == Visibility.Visible && LibraryPanel.Visibility == Visibility.Collapsed);
            await CloseNowPlayingAsync();
            UpdateAdaptiveLayout(800);
            Verify("compact_browse_restored", TrackList.IsEnabled && CollectionList.IsEnabled);
            await OpenNowPlayingAsync();
            UpdateAdaptiveLayout(1360);
            Verify("wide_lyrics_two_columns", Microsoft.UI.Xaml.Controls.Grid.GetRow(TransportPanel) == 0
                && Microsoft.UI.Xaml.Controls.Grid.GetColumn(FullLyricsText) == 1 && FullArtworkPanel.Visibility == Visibility.Visible);
            await CloseNowPlayingAsync();
            UpdateAdaptiveLayout(Root.ActualWidth);
            var activeLocalAudio = music.Player.Source;
            var localItems = TrackList.ItemsSource;
            ResetLibrary();
            Verify("nas_reset_preserves_local_view_and_playback", ReferenceEquals(music.Player.Source, activeLocalAudio) && mediaReady
                && queue.Count == 1 && ReferenceEquals(TrackList.ItemsSource, localItems) && searchQuery == "000-playback" && localView);
            await SwitchCollectionAsync(null);
            ResetLibrary();
            Verify("nas_reset_clears_remote_view_only", TrackList.ItemsSource is null && total == 0 && queue.Count == 1
                && ReferenceEquals(music.Player.Source, activeLocalAudio) && mediaReady);
            await VerifyStoppedLyricsRequestAsync(Verify, queue.Current ?? throw new InvalidOperationException());
            Verify("stop_clears_presentation", NowCover.Source is null && currentLyrics.Lines.Count == 0 && LyricsList.ItemsSource is null);
            await SwitchCollectionAsync(null);
            Verify("local_return_to_nas", !localView && TrackList.SelectedItem is MusicTrack { Id: "synthetic-a" } && ViewModel.PlaybackRouter.CanPlay(local.Tracks[0].Reference));
            var remoteTrack = TrackList.SelectedItem as MusicTrack ?? throw new InvalidOperationException();
            queue.Replace([remoteTrack], remoteTrack.Reference);
            await ShowLocalMusicAsync();
            localItems = TrackList.ItemsSource;
            ResetLibrary();
            Verify("nas_reset_removes_remote_queue_keeps_local_view", queue.Count == 0 && music.Player.Source is null
                && ReferenceEquals(TrackList.ItemsSource, localItems) && total == 53 && localView);
            var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingScan = ScanLocalAsync(local.Settings, default, async (_, ct) =>
            { scanEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new("", "", 0); });
            await scanEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Verify("local_scan_cancel_visible", CancelLocalScan.Visibility == Visibility.Visible);
            CancelLocalScan_Click(this, new RoutedEventArgs());
            try { await pendingScan; throw new InvalidOperationException(); } catch (OperationCanceledException) { }
            Verify("local_scan_cancel_preserves_source", ReferenceEquals(localSource, local) && CancelLocalScan.Visibility == Visibility.Collapsed
                && LocalScanStatus.Text.Contains("已取消") && ViewModel.PlaybackRouter.CanPlay(local.Tracks[0].Reference));
            var storedLibraries = await localLibraryStore.LoadAsync();
            ApplyLocalCatalog(await localLibraryStore.SaveAsync(localCatalog with { Revision = storedLibraries.Revision }));
            await LoadPageAsync(2);
            TrackList.SelectedIndex = 1;
            var beforeRefreshItems = TrackList.ItemsSource;
            var beforeRefreshSelection = TrackList.SelectedItem;
            var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            localPageMetadataReader = async (_, ct) =>
            { refreshEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new("", "", 0); };
            try
            {
                var refresh = LoadLocalPageAsync(1, true);
                await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Verify("local_refresh_loading", TrackList.ItemsSource is null && CancelLocalScan.Visibility == Visibility.Visible);
                CancelLocalScan_Click(this, new RoutedEventArgs());
                await refresh;
                Verify("local_refresh_cancel_restores_page_selection", ReferenceEquals(TrackList.ItemsSource, beforeRefreshItems)
                    && ReferenceEquals(TrackList.SelectedItem, beforeRefreshSelection) && page == 2 && total == 53
                    && PreviousPage.IsEnabled && !NextPage.IsEnabled && PageStatus.Text.Contains("已取消"));
                Verify("local_refresh_cancel_keeps_registration", ReferenceEquals(localSources[local.Source.Id], local)
                    && ViewModel.PlaybackRouter.CanPlay(local.Tracks[0].Reference));
                TrackList.ItemsSource = new[] { remoteTrack }; TrackList.SelectedIndex = 0;
                refreshEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                refresh = LoadLocalPageAsync(1, true);
                await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                CancelLocalScan_Click(this, new RoutedEventArgs()); await refresh;
                Verify("local_cancel_does_not_restore_foreign_page", TrackList.ItemsSource is null && TrackList.SelectedItem is null
                    && page == 1 && total == 0 && !PreviousPage.IsEnabled && !NextPage.IsEnabled);
            }
            finally { localPageMetadataReader = null; }
            SearchInput.Text = "000-playback"; await SearchAsync();
            await VerifyLocalPlaylistWindowAsync(Verify, TrackList.SelectedItem as MusicTrack ?? throw new InvalidOperationException());
            await VerifyMultiLibraryWindowAsync(Verify);
            await VerifyNasPlaylistWindowAsync(Verify);
            await VerifyNasSettingsWindowAsync(Verify);
            await VerifyPlaylistPageNavigationAsync(Verify);
            await VerifyUiMotionAsync(Verify);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally { motionPreferenceOverride = null; }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "collection-window-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, timings }));
    }
    private sealed class CapabilityPlaybackSource(IMusicPlaybackSource source, MusicSourceCapabilities capabilities) : IMusicPlaybackSource
    {
        public MusicSource Source { get; } = source.Source with { Capabilities = capabilities };
        public Task<Stream> OpenAsync(TrackReference track, CancellationToken ct) => source.OpenAsync(track, ct);
    }
}
#endif
