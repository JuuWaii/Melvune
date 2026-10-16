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
        var button = FindFullPageButton(view, label);
        if (!button.IsEnabled) throw new InvalidOperationException("Fixture control disabled: " + label);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(50);
        for (int i = 0; i < 200 && !view.Editor.IsEnabled; i++) await Task.Delay(50);
        if (!view.Editor.IsEnabled) throw new TimeoutException();
    }
    private static Button FindFullPageButton(PlaylistPageView view, string label) => view.Actions.Children.OfType<Button>()
        .Concat(view.HeadingActions?.Children.OfType<Button>() ?? [])
        .Single(item => item.Tag?.ToString() == label || item.Content.ToString() == label);

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
                && view.List.SelectedItem is LibraryManagerRow { Local: { } selected } && selected.SourceId == selectedLibrary.SourceId);
            await VerifyFullPageLayoutAsync(verify, view, "local_library");
            Root.UpdateLayout();
            var firstCard = (view.List.ContainerFromIndex(0) as ListViewItem)?.ContentTemplateRoot;
            verify("local_library_cards_show_name_and_directory", ReferenceEquals(view.List.ItemTemplate, Root.Resources["LocalLibraryCardTemplate"])
                && firstCard is not null && FindSongVisual<TextBlock>(firstCard, text => text.Text == selectedLibrary.Name) is { ActualWidth: > 0 }
                && FindSongVisual<TextBlock>(firstCard, text => text.Text == selectedLibrary.Directory) is { ActualWidth: > 0 });
            await VerifyLibraryHeadingAsync(verify, view);
            await VerifyLibraryCardScanAsync(verify, view);
            await VerifyLibraryEditorWindowAsync(verify, view, selectedLibrary, audio);
            view = await VerifyBackgroundLibraryWindowAsync(verify, view);
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

    private async Task VerifyLibraryHeadingAsync(Action<string, bool> verify, PlaylistPageView view)
    {
        var size = AppWindow.Size;
        try
        {
            AppWindow.Resize(new global::Windows.Graphics.SizeInt32(820, 560)); await Task.Delay(100); Root.UpdateLayout();
            var add = FindFullPageButton(view, "新建音乐库"); var scan = FindFullPageButton(view, "扫描音乐库");
            var origin = scan.TransformToVisual(view.Layout).TransformPoint(new global::Windows.Foundation.Point());
            verify("local_library_heading_actions_fit_compact_window", add.IsLoaded && scan.IsLoaded && add.ActualWidth > 0 && scan.ActualWidth > 0
                && origin.X + scan.ActualWidth <= view.Layout.ActualWidth + 1 && origin.Y >= 0 && origin.Y < 50
                && view.TitleText.Text == "音乐管理" && view.TitleText.ActualWidth >= 4 * view.TitleText.FontSize - 1 && view.CancelButton.Visibility == Visibility.Collapsed);
        }
        finally { AppWindow.Resize(size); await Task.Delay(100); Root.UpdateLayout(); }
    }

    private async Task VerifyLibraryCardScanAsync(Action<string, bool> verify, PlaylistPageView view)
    {
        var catalog = await localLibraryStore.LoadAsync();
        // The preceding removal fixture intentionally leaves one in-memory source unregistered.
        ApplyLocalCatalog(catalog);
        await RestoreLocalSourcesAsync(default);
        var oldSources = catalog.Libraries.ToDictionary(item => item.SourceId, item => localSources[item.SourceId]);
        var audio = music.Player.Source;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = catalog.Libraries.Skip(1).First();
        localPageMetadataReader = async (file, ct) =>
        {
            if (file.StartsWith(Path.TrimEndingDirectorySeparator(second.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return new("扫描夹具", "夹具", 60);
        };
        try
        {
            ((IInvokeProvider)new ButtonAutomationPeer(FindFullPageButton(view, "扫描音乐库")).GetPattern(PatternInterface.Invoke)).Invoke();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            verify("local_library_cards_scan_allows_navigation_and_blocks_duplicate_writes", !view.BlockNavigation && view.BackButton.IsEnabled && MetadataActivity.IsActive
                && view.CancelButton.IsEnabled && view.CancelButton.Visibility == Visibility.Visible
                && !FindFullPageButton(view, "新建音乐库").IsEnabled);
            ((IInvokeProvider)new ButtonAutomationPeer(view.CancelButton).GetPattern(PatternInterface.Invoke)).Invoke();
            for (int i = 0; i < 100 && !view.Editor.IsEnabled; i++) await Task.Delay(50);
            verify("local_library_cards_scan_cancel_discards_all_staged_sources", view.Editor.IsEnabled && view.Status.Text.Contains("取消")
                && oldSources.All(item => ReferenceEquals(localSources[item.Key], item.Value))
                && (await localLibraryStore.LoadAsync()).Revision == catalog.Revision && ReferenceEquals(audio, music.Player.Source));
        }
        finally { localPageMetadataReader = null; }
        await ClickFullPageButtonAsync(view, "扫描音乐库");
        verify("local_library_cards_scan_applies_all_sources_without_catalog_write", view.Status.Text.Contains("扫描完成")
            && catalog.Libraries.All(item => !ReferenceEquals(localSources[item.SourceId], oldSources[item.SourceId]))
            && (await localLibraryStore.LoadAsync()).Revision == catalog.Revision && ReferenceEquals(audio, music.Player.Source));

        oldSources = catalog.Libraries.ToDictionary(item => item.SourceId, item => localSources[item.SourceId]);
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        localPageMetadataReader = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return new("过期扫描", "夹具", 60); };
        try
        {
            ((IInvokeProvider)new ButtonAutomationPeer(FindFullPageButton(view, "扫描音乐库")).GetPattern(PatternInterface.Invoke)).Invoke();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var external = await localLibraryStore.SaveAsync(catalog);
            release.TrySetResult();
            for (int i = 0; i < 100 && !view.Editor.IsEnabled; i++) await Task.Delay(50);
            verify("local_library_cards_scan_conflict_preserves_sources_and_catalog", view.Editor.IsEnabled && view.Status.Text.Contains("刷新")
                && oldSources.All(item => ReferenceEquals(localSources[item.Key], item.Value))
                && (await localLibraryStore.LoadAsync()).Revision == external.Revision && ReferenceEquals(audio, music.Player.Source));
            await ClickFullPageButtonAsync(view, "刷新音乐库");
        }
        finally { release.TrySetResult(); localPageMetadataReader = null; }
    }
}
#endif
