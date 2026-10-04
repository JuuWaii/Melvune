#if DEBUG
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private async Task VerifyFullPageLayoutAsync(Action<string, bool> verify, PlaylistPageView view, string prefix)
    {
        bool FooterFits()
        {
            Root.UpdateLayout();
            var status = view.Status.TransformToVisual(view.Layout).TransformPoint(new global::Windows.Foundation.Point());
            var actions = view.Actions.TransformToVisual(view.Layout).TransformPoint(new global::Windows.Foundation.Point());
            return status.Y >= 0 && status.Y + view.Status.ActualHeight <= view.Layout.ActualHeight + 1
                && actions.Y >= 0 && actions.Y + view.Actions.ActualHeight <= view.Layout.ActualHeight + 1;
        }
        verify(prefix + "_full_page_footer_visible", FooterFits());
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            Root.RequestedTheme = theme; Root.UpdateLayout();
            verify(prefix + "_opaque_full_page_" + theme, VisualTreeHelper.GetChildrenCount(FullPageHost) > 0
                && VisualTreeHelper.GetChild(FullPageHost, 0) is Border { Background: SolidColorBrush brush }
                && brush.Color.A == 255 && view.Layout.ActualTheme == theme);
        }
        ApplySystemTheme();
        var size = AppWindow.Size;
        try
        {
            AppWindow.Resize(new global::Windows.Graphics.SizeInt32(820, 560)); await Task.Delay(100);
            verify(prefix + "_compact_full_page_footer_visible", FooterFits() && view.BackButton.IsLoaded);
        }
        finally { AppWindow.Resize(size); await Task.Delay(100); Root.UpdateLayout(); }
    }

    private async Task ClickFullPageButtonAsync(PlaylistPageView view, string label)
    {
        var button = view.Actions.Children.OfType<Button>().Single(item => item.Content.ToString() == label);
        if (!button.IsEnabled) throw new InvalidOperationException("Fixture control disabled: " + label);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(50);
        for (int i = 0; i < 200 && !view.Editor.IsEnabled; i++) await Task.Delay(50);
        if (!view.Editor.IsEnabled) throw new TimeoutException();
    }

    private async Task VerifyQueueDrawerLayoutAsync(Action<string, bool> verify, PlaylistPageView view)
    {
        bool Fits()
        {
            Root.UpdateLayout();
            var pane = QueuePane.TransformToVisual(Root).TransformPoint(new global::Windows.Foundation.Point());
            var actions = view.Actions.TransformToVisual(QueuePane).TransformPoint(new global::Windows.Foundation.Point());
            var status = view.Status.TransformToVisual(QueuePane).TransformPoint(new global::Windows.Foundation.Point());
            return QueuePane.ActualWidth <= 420 && Math.Abs(pane.X + QueuePane.ActualWidth - Root.ActualWidth + 8) < 2
                && pane.Y + QueuePane.ActualHeight <= Root.ActualHeight - PlayerBar.ActualHeight + 1
                && actions.Y + view.Actions.ActualHeight <= QueuePane.ActualHeight && status.Y + view.Status.ActualHeight <= QueuePane.ActualHeight;
        }
        verify("queue_drawer_right_overlay_footer_and_player_fit", Fits());
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            Root.RequestedTheme = theme; Root.UpdateLayout();
            verify("queue_drawer_opaque_theme_" + theme, QueuePane.Background is SolidColorBrush brush && brush.Color.A == 255 && view.Layout.ActualTheme == theme);
        }
        ApplySystemTheme();
        var size = AppWindow.Size;
        try
        {
            AppWindow.Resize(new global::Windows.Graphics.SizeInt32(820, 560)); await Task.Delay(150);
            verify("queue_drawer_compact_footer_and_player_fit", Fits() && view.List.ActualHeight >= view.List.MinHeight);
        }
        finally { AppWindow.Resize(size); await Task.Delay(100); Root.UpdateLayout(); }
        verify("queue_drawer_track_template_has_metadata_and_artwork", ReferenceEquals(view.List.ItemTemplate, Root.Resources["QueueTrackTemplate"])
            && view.List.ContainerFromIndex(0) is ListViewItem { ContentTemplateRoot: FrameworkElement row }
            && row.FindName("QueueCurrentMarker") is FontIcon marker && marker.Visibility == Visibility.Visible);
    }

    private async Task VerifyManagementFullPagesAsync(Action<string, bool> verify)
    {
        await ShowLocalMusicAsync();
        var firstTrack = ((IEnumerable<MusicTrack>)TrackList.ItemsSource).First();
        await StartQueueAsync(firstTrack);
        for (int i = 0; i < 100 && !mediaReady; i++) await Task.Delay(50);
        verify("management_full_page_playback_fixture_ready", mediaReady && music.Player.Source is not null);
        music.Player.Pause();
        var oldItems = TrackList.ItemsSource; var oldSelected = TrackList.SelectedItem;
        var audio = music.Player.Source; var current = queue.Current?.Reference;
        var presenter = AppWindow.Presenter.Kind; var windowSize = AppWindow.Size;
        motionPreferenceOverride = true;
        ShowQueuePage();
        var view = queuePage ?? throw new InvalidOperationException();
        verify("queue_drawer_entry_slides_from_right", uiMotions.ContainsKey(QueuePane) && FindUiMotionTransform(QueuePane)?.TranslateX > 0);
        await Task.Delay(320);
        verify("queue_drawer_entry_settles", !uiMotions.ContainsKey(QueuePane) && FindUiMotionTransform(QueuePane)?.TranslateX == 0 && QueuePane.Opacity == 1);
        motionPreferenceOverride = false;
        verify("queue_drawer_preserves_browse_and_player", fullPageView is null && NormalContent.IsEnabled && LyricsInteractionHost.IsEnabled
            && LibraryPanel.Visibility == Visibility.Visible && PlayerBar.Visibility == Visibility.Visible && PlayerBar.IsLoaded && !themedDialogOpen
            && AppWindow.Presenter.Kind == presenter && AppWindow.Size == windowSize && ReferenceEquals(audio, music.Player.Source)
            && ReferenceEquals(oldItems, TrackList.ItemsSource) && ReferenceEquals(oldSelected, TrackList.SelectedItem));
        await VerifyQueueDrawerLayoutAsync(verify, view);
        var offline = new MusicTrack("queue-offline", "离线队列测试", "夹具", 10, false) { SourceInstanceId = Guid.NewGuid() };
        queue.InsertNext(offline); UpdateQueueStatus();
        verify("queue_drawer_live_insert", view.List.Items.OfType<MusicTrack>().Any(item => item.Reference == offline.Reference));
        view.List.SelectedItem = view.List.Items.OfType<MusicTrack>().Single(item => item.Reference == offline.Reference);
        verify("queue_drawer_offline_play_disabled", !view.PlayButton.IsEnabled);
        await ClickFullPageButtonAsync(view, "移除所选");
        verify("queue_drawer_remove_other_preserves_playback", !queue.Tracks.Any(item => item.Reference == offline.Reference)
            && ReferenceEquals(audio, music.Player.Source) && queue.Current?.Reference == current);
        view.BlockNavigation = true;
        verify("queue_drawer_write_blocks_close", !CloseQueuePage() && ReferenceEquals(queuePage, view) && !view.IsDisposed);
        view.BlockNavigation = false;
        CloseQueuePage();
        verify("queue_drawer_close_preserves_browse", view.IsDisposed && QueueOverlay.Visibility == Visibility.Collapsed
            && ReferenceEquals(oldItems, TrackList.ItemsSource) && ReferenceEquals(oldSelected, TrackList.SelectedItem));
        await OpenNowPlayingAsync();
        ShowQueuePage(); view = queuePage ?? throw new InvalidOperationException();
        verify("queue_drawer_over_lyrics_preserves_audio", LyricsPanel.Visibility == Visibility.Visible && LyricsInteractionHost.IsEnabled
            && ReferenceEquals(audio, music.Player.Source) && queue.Current?.Reference == current && QueueOverlay.Margin.Bottom == 0);
        // Present the newly mounted surface before checking its ordinary close transition.
        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int presentationFrames = 0;
        void Presented(object? sender, object args) { if (++presentationFrames >= 2) presented.TrySetResult(); }
        CompositionTarget.Rendering += Presented;
        try { await presented.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= Presented; }
        motionPreferenceOverride = true;
        var exitClock = System.Diagnostics.Stopwatch.StartNew();
        var samples = new List<(long Milliseconds, double X)>();
        bool movedRight = false;
        void SampleExit(object? sender, object args)
        {
            double x = FindUiMotionTransform(QueuePane)?.TranslateX ?? 0;
            samples.Add((exitClock.ElapsedMilliseconds, x));
            if (x > 0 && x < QueuePane.ActualWidth + 8 && QueueOverlay.Visibility == Visibility.Visible) movedRight = true;
        }
        CompositionTarget.Rendering += SampleExit;
        Task exit;
        try
        {
            exit = CloseQueuePageAsync();
            verify("queue_drawer_close_starts_horizontal_motion", uiMotions.ContainsKey(QueuePane) && !view.Editor.IsEnabled && !QueuePaneHost.IsEnabled);
            await exit;
        }
        finally
        {
            CompositionTarget.Rendering -= SampleExit;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "queue-motion-frame.json"),
                System.Text.Json.JsonSerializer.Serialize(samples.Select(sample => new { elapsed = sample.Milliseconds, x = sample.X })));
        }
        verify("queue_drawer_close_moves_right", movedRight);
        verify("queue_drawer_returns_to_same_lyrics", view.IsDisposed && queuePage is null && LyricsPanel.Visibility == Visibility.Visible
            && LyricsInteractionHost.IsEnabled && !NormalContent.IsEnabled && ReferenceEquals(audio, music.Player.Source));
        ShowQueuePage(); view = queuePage ?? throw new InvalidOperationException();
        exit = CloseQueuePageAsync();
        ShowQueuePage(); var reopened = queuePage ?? throw new InvalidOperationException();
        await exit; await Task.Delay(300);
        verify("queue_drawer_reopen_ignores_old_close", view.IsDisposed && !ReferenceEquals(view, reopened) && !reopened.IsDisposed
            && ReferenceEquals(queuePage, reopened) && QueueOverlay.Visibility == Visibility.Visible);
        await CloseQueuePageAsync();
        verify("queue_drawer_reopened_page_can_close", queuePage is null && reopened.IsDisposed && !uiMotions.ContainsKey(QueuePane));
        motionPreferenceOverride = false;
        ShowQueuePage();
        verify("queue_drawer_disabled_motion_settles_immediately", !uiMotions.ContainsKey(QueuePane) && FindUiMotionTransform(QueuePane)?.TranslateX == 0);
        await CloseQueuePageAsync();
        await CloseNowPlayingAsync();
        var catalog = await localLibraryStore.LoadAsync();
        var before = await playlistStore.LoadAsync();
        var selectedLibrary = catalog.Libraries.First();
        localLibraryPageOpened = pageView => verify("local_library_full_page_hook_loaded", ReferenceEquals(fullPageView, pageView));
        try
        {
            selectedLocalLibrary = selectedLibrary.SourceId;
            await ShowLocalLibraryManagementAsync(); view = fullPageView ?? throw new InvalidOperationException();
            verify("local_library_full_page_no_dialog", !themedDialogOpen && view.List.Items.Count == catalog.Libraries.Count
                && view.List.SelectedItem is LocalLibrary selected && selected.SourceId == selectedLibrary.SourceId);
            await VerifyFullPageLayoutAsync(verify, view, "local_library");
            var name = view.Fields.Children.OfType<TextBox>().Single(box => box.Header.ToString() == "库名称");
            var group = view.Fields.Children.OfType<TextBox>().Single(box => box.Header.ToString() == "分组（可选）");
            var path = view.Fields.Children.OfType<TextBox>().Single(box => box.Header.ToString() == "文件夹路径");
            verify("local_library_remove_requires_confirmation", !view.Actions.Children.OfType<Button>().Single(button => button.Content.ToString() == "移除音乐库").IsEnabled);
            name.Text = "页面改名夹具"; group.Text = "页面分组";
            await ClickFullPageButtonAsync(view, "保存");
            var renamed = await localLibraryStore.LoadAsync();
            verify("local_library_full_page_rename_group_persisted", renamed.Libraries.Any(item => item.SourceId == selectedLibrary.SourceId
                && item.Name == "页面改名夹具" && item.Group == "页面分组") && ReferenceEquals(audio, music.Player.Source));
            await localLibraryStore.SaveAsync(renamed with { Libraries = renamed.Libraries.Select(item => item.SourceId == selectedLibrary.SourceId ? item with { Name = "外部更新" } : item).ToArray() });
            name.Text = "过期名称"; await ClickFullPageButtonAsync(view, "保存");
            verify("local_library_full_page_conflict_preserves_external", (await localLibraryStore.LoadAsync()).Libraries.Single(item => item.SourceId == selectedLibrary.SourceId).Name == "外部更新"
                && view.Status.Text.Contains("刷新") && !view.Actions.Children.OfType<Button>().Single(button => button.Content.ToString() == "保存").IsEnabled);
            await ClickFullPageButtonAsync(view, "刷新音乐库");
            verify("local_library_full_page_refresh_recovers", name.Text == "外部更新");
            path.Text = Path.Combine(AppContext.BaseDirectory, "missing-library-" + Guid.NewGuid().ToString("N"));
            long revision = (await localLibraryStore.LoadAsync()).Revision;
            await ClickFullPageButtonAsync(view, "保存");
            verify("local_library_full_page_missing_relink_preserves_catalog", (await localLibraryStore.LoadAsync()).Revision == revision
                && view.Status.Text.Contains("原音乐库保留") && ReferenceEquals(audio, music.Player.Source));
            await ClickFullPageButtonAsync(view, "刷新音乐库");
            string moved = Path.Combine(AppContext.BaseDirectory, "library-page-relocated");
            Directory.CreateDirectory(moved);
            var originalFiles = Directory.GetFiles(selectedLibrary.Directory);
            var originalHashes = originalFiles.ToDictionary(file => file, file => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
            foreach (var file in originalFiles) File.Copy(file, Path.Combine(moved, Path.GetFileName(file)), true);
            var reference = localSources[selectedLibrary.SourceId].Tracks.First().Reference;
            path.Text = moved;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            localPageMetadataReader = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new("取消夹具", "夹具", 60); };
            try
            {
                var save = view.Actions.Children.OfType<Button>().Single(button => button.Content.ToString() == "保存");
                ((IInvokeProvider)new ButtonAutomationPeer(save).GetPattern(PatternInterface.Invoke)).Invoke();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                verify("local_library_full_page_scan_can_cancel", view.CancelButton.Visibility == Visibility.Visible && view.CancelButton.IsEnabled
                    && view.BlockNavigation && !CloseFullPage());
                ((IInvokeProvider)new ButtonAutomationPeer(view.CancelButton).GetPattern(PatternInterface.Invoke)).Invoke();
                for (int i = 0; i < 100 && !view.Editor.IsEnabled; i++) await Task.Delay(50);
                verify("local_library_full_page_cancel_preserves_catalog", view.Editor.IsEnabled && !view.BlockNavigation && (await localLibraryStore.LoadAsync()).Revision == revision
                    && view.Status.Text.Contains("取消") && ReferenceEquals(audio, music.Player.Source));
            }
            finally { localPageMetadataReader = null; }
            await ClickFullPageButtonAsync(view, "保存");
            verify("local_library_full_page_relink_preserves_identity", (await localLibraryStore.LoadAsync()).Libraries.Single(item => item.SourceId == selectedLibrary.SourceId).Directory == moved
                && localSources[selectedLibrary.SourceId].Tracks.Any(track => track.Reference == reference));
            verify("local_library_full_page_original_files_unchanged", originalHashes.All(item => File.Exists(item.Key)
                && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value));
            revision = (await localLibraryStore.LoadAsync()).Revision;
            await ClickFullPageButtonAsync(view, "添加音乐库");
            verify("local_library_full_page_add_form_no_implicit_write", view.List.SelectedItem is null && path.Text == ""
                && (await localLibraryStore.LoadAsync()).Revision == revision);
            view.BlockNavigation = true;
            verify("management_full_page_write_blocks_back", !CloseFullPage() && ReferenceEquals(fullPageView, view) && !view.IsDisposed);
            view.BlockNavigation = false;
            verify("local_library_full_page_preserves_playlist_references", (await playlistStore.LoadAsync()).Revision == before.Revision);
        }
        finally
        {
            CloseFullPage(force: true); localLibraryPageOpened = null;
            var latest = await localLibraryStore.LoadAsync();
            var restored = await localLibraryStore.SaveAsync(latest with { Libraries = catalog.Libraries });
            ApplyLocalCatalog(restored);
            await RestoreLocalSourcesAsync(default);
        }
        verify("management_full_page_back_cleanup", fullPageView is null && FullPageHost.Content is null
            && NormalContent.IsEnabled && LyricsInteractionHost.IsEnabled);
    }
}
#endif
