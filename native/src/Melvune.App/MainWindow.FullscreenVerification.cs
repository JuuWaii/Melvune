#if DEBUG
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Melvune.Core;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private async Task VerifyFullscreenPresentationAsync(Action<string, bool> verify)
    {
        await CloseNowPlayingAsync();
        string previousSearch = SearchInput.Text;
        int previousPage = page;
        var previousSelection = (TrackList.SelectedItem as Melvune.Core.MusicTrack)?.Reference;
        if (TrackList.Items.Count <= 20)
        {
            SearchInput.Text = "";
            await SearchAsync();
        }
        var presenter = AppWindow.Presenter;
        var originalSize = AppWindow.Size;
        var originalPosition = AppWindow.Position;
        var items = TrackList.ItemsSource;
        var selected = TrackList.SelectedItem;
        ScrollViewer? FindScroll(DependencyObject parent)
        {
            if (parent is ScrollViewer scroll) return scroll;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
            return null;
        }
        var browseScroll = FindScroll(TrackList);
        double initialOffset = browseScroll?.VerticalOffset ?? 0;
        if (TrackList.Items.Count > 20) TrackList.ScrollIntoView(TrackList.Items[^1]);
        await Task.Delay(100);
        double browseOffset = browseScroll?.VerticalOffset ?? 0;
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "presentation-scroll-fixture.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Items = TrackList.Items.Count, Offset = browseOffset,
                ScrollableHeight = browseScroll?.ScrollableHeight, TrackVisibility = TrackList.Visibility.ToString() }));
        var audio = music.Player.Source;
        var current = queue.Current?.Reference;
        int queueSize = queue.Count;
        int originalPage = page;
        var libraryVisibility = LibraryPanel.Visibility;
        var settingsVisibility = SettingsPanel.Visibility;
        NowCoverButton.Focus(FocusState.Programmatic);
        await OpenNowPlayingAsync();
        verify("now_playing_internal_page_preserves_window_and_hides_background", AppWindow.Presenter.Kind == presenter.Kind
            && AppWindow.Size.Width == originalSize.Width && AppWindow.Size.Height == originalSize.Height
            && AppWindow.Position.X == originalPosition.X && AppWindow.Position.Y == originalPosition.Y
            && LyricsPanel.Visibility == Visibility.Visible && SidebarHost.Visibility == Visibility.Collapsed
            && LibraryPanel.Visibility == Visibility.Collapsed && SettingsPanel.Visibility == Visibility.Collapsed);
        verify("now_playing_escape_wired_and_return_focused", Root.KeyboardAccelerators.Any(key => key.Key == global::Windows.System.VirtualKey.Escape)
            && ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), NowPlayingBack));
        verify("now_playing_reuses_transport_volume_and_output_controls", ReferenceEquals(FullTransportHost.Content, TransportPanel)
            && TransportPanel.Children.Contains(PlayerOptionsPanel) && PlayerOptionsPanel.Children.Contains(OutputDeviceButton)
            && !PlayerLayout.Children.Contains(TransportPanel) && PlayerBar.Visibility == Visibility.Collapsed
            && Microsoft.UI.Xaml.Controls.Grid.GetRowSpan(LyricsPanel) == 2);
        verify("now_playing_uses_current_presentation_and_same_audio", FullNowTitle.Text == ((Melvune.Core.MusicTrack)NowCover.DataContext).Title
            && ReferenceEquals(music.Player.Source, audio) && queue.Current?.Reference == current && queue.Count == queueSize);
        PlayPause_Click(this, new RoutedEventArgs());
        await Task.Delay(100);
        UpdateProgress();
        verify("now_playing_shared_play_button_resumes_audio", music.Player.PlaybackSession.PlaybackState == global::Windows.Media.Playback.MediaPlaybackState.Playing
            && PlayPauseIcon.Glyph == "\uE769" && ReferenceEquals(music.Player.Source, audio));
        PlayPause_Click(this, new RoutedEventArgs());
        await Task.Delay(100);
        UpdateProgress();
        verify("now_playing_shared_play_button_pauses_audio", music.Player.PlaybackSession.PlaybackState == global::Windows.Media.Playback.MediaPlaybackState.Paused
            && PlayPauseIcon.Glyph == "\uE768" && ReferenceEquals(music.Player.Source, audio));
        Root.UpdateLayout();
        var activeText = (LyricsList.ContainerFromIndex(activeLyric) as ListViewItem)?.ContentTemplateRoot as TextBlock;
        var inactiveText = (LyricsList.ContainerFromIndex(activeLyric == 0 ? 1 : 0) as ListViewItem)?.ContentTemplateRoot as TextBlock;
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "lyric-emphasis-diagnostic.json"),
            System.Text.Json.JsonSerializer.Serialize(new { ActiveIndex = activeLyric, Timed = currentLyrics.IsTimed,
                ActiveRoot = (LyricsList.ContainerFromIndex(activeLyric) as ListViewItem)?.ContentTemplateRoot?.GetType().Name,
                InactiveRoot = (LyricsList.ContainerFromIndex(activeLyric == 0 ? 1 : 0) as ListViewItem)?.ContentTemplateRoot?.GetType().Name,
                ActiveOpacity = activeText?.Opacity, InactiveOpacity = inactiveText?.Opacity,
                ActiveWeight = activeText?.FontWeight.Weight, InactiveWeight = inactiveText?.FontWeight.Weight }));
        verify("now_playing_current_lyric_emphasized", activeText is { Opacity: 1 }
            && activeText.FontWeight == global::Microsoft.UI.Text.FontWeights.SemiBold && inactiveText is not null
            && Math.Abs(inactiveText.Opacity - .55) < .001);
        await LoadArtworkAsync(FullNowCover);
        await Task.Delay(50);
        verify("now_playing_cover_uses_current_context_and_cache", FullNowCover.Source is not null
            && ReferenceEquals(FullNowCover.DataContext, NowCover.DataContext) && coverCache.Values.Contains(FullNowCover.Source));
        UpdateCollectionView();
        verify("now_playing_survives_browser_state_update", LyricsPanel.Visibility == Visibility.Visible
            && LibraryPanel.Visibility == Visibility.Collapsed && AppWindow.Presenter.Kind == presenter.Kind);
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            Root.RequestedTheme = theme;
            verify("now_playing_theme_" + theme, LyricsPanel.ActualTheme == theme);
        }
        await CloseNowPlayingAsync();
        await Task.Delay(100);
        verify("now_playing_return_preserves_window_and_restores_controls", AppWindow.Presenter.Kind == presenter.Kind
            && AppWindow.Size.Width == originalSize.Width && AppWindow.Size.Height == originalSize.Height
            && PlayerLayout.Children.Contains(TransportPanel) && TransportPanel.Children.Contains(PlayerOptionsPanel)
            && PlayerOptionsPanel.Children.Contains(OutputDeviceButton) && FullTransportHost.Content is null
            && PlayerBar.Visibility == Visibility.Visible);
        verify("now_playing_return_keeps_page_selection_and_playback", ReferenceEquals(TrackList.ItemsSource, items)
            && ReferenceEquals(TrackList.SelectedItem, selected) && page == originalPage
            && LibraryPanel.Visibility == libraryVisibility && SettingsPanel.Visibility == settingsVisibility
            && SidebarHost.Visibility == Visibility.Visible && ReferenceEquals(music.Player.Source, audio) && queue.Current?.Reference == current);
        verify("now_playing_return_preserves_scroll", browseScroll is not null && browseOffset > 0
            && Math.Abs(browseScroll.VerticalOffset - browseOffset) < 1);
        verify("now_playing_return_preserves_focus", ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), NowCoverButton));
        browseScroll?.ChangeView(null, initialOffset, null, true);
        // Returning must preserve a settings page too; entering lyrics is not library navigation.
        await ShowSettingsPanelAsync();
        await OpenNowPlayingAsync();
        await CloseNowPlayingAsync();
        verify("now_playing_returns_to_settings_without_browse_navigation", SettingsPanel.Visibility == Visibility.Visible
            && LibraryPanel.Visibility == Visibility.Collapsed && ReferenceEquals(TrackList.ItemsSource, items) && page == originalPage);
        SettingsPanel.Visibility = settingsVisibility;
        LibraryPanel.Visibility = libraryVisibility;
        if (presenter is OverlappedPresenter overlapped)
        {
            overlapped.Maximize();
            await OpenNowPlayingAsync();
            verify("now_playing_keeps_maximized_window_while_open", AppWindow.Presenter is OverlappedPresenter openPresenter
                && openPresenter.State == OverlappedPresenterState.Maximized);
            await CloseNowPlayingAsync();
            verify("now_playing_return_keeps_maximized_window", AppWindow.Presenter is OverlappedPresenter returned
                && returned.State == OverlappedPresenterState.Maximized);
            overlapped.Restore();
            AppWindow.Resize(originalSize);
        }
        SearchInput.Text = previousSearch;
        await SearchAsync();
        if (previousPage != 1) await LoadPageAsync(previousPage);
        if (previousSelection is { } reference && TrackList.ItemsSource is IEnumerable<Melvune.Core.MusicTrack> restored)
            TrackList.SelectedItem = restored.FirstOrDefault(track => track.Reference == reference);
        await OpenNowPlayingAsync();
    }

    private async Task VerifyPlayerControlsAsync(Action<string, bool> verify)
    {
        var size = AppWindow.Size;
        var mode = queue.Mode;
        double volume = music.Player.Volume;
        bool muted = music.Player.IsMuted;
        var audio = music.Player.Source;
        var device = music.Player.AudioDevice;
        async Task<bool> WaitForPlayerFramesAsync()
        {
            var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int frames = 0;
            void Presented(object? sender, object args) { if (++frames >= 2) presented.TrySetResult(); }
            CompositionTarget.Rendering += Presented;
            try { await Task.WhenAny(presented.Task, Task.Delay(2000)); return presented.Task.IsCompleted; }
            finally { CompositionTarget.Rendering -= Presented; }
        }
        bool ControlsFit()
        {
            Root.UpdateLayout();
            var buttons = new FrameworkElement[] { QueueModeButton, PreviousTrackButton, PlayPauseButton, NextTrackButton,
                StopButton, OutputDeviceButton, MuteToggle, VolumeSlider, QueueButton };
            bool Failure(string reason)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "player-layout-failure.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason, rootWidth = Root.ActualWidth, lyrics = LyricsPanel.Visibility.ToString(), transportWidth = TransportPanel.ActualWidth,
                    controls = buttons.Select(control => new { control.Name, control.IsLoaded, bounds = control.TransformToVisual(TransportPanel).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight)) })
                }));
                return false;
            }
            double center = PlayPauseButton.TransformToVisual(TransportPanel).TransformPoint(new(0, PlayPauseButton.ActualHeight / 2)).Y;
            foreach (var control in buttons)
            {
                var bounds = control.TransformToVisual(TransportPanel).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight));
                if (!control.IsLoaded || bounds.Width <= 0 || bounds.X < -1 || bounds.Right > TransportPanel.ActualWidth + 1
                    || Math.Abs(bounds.Y + bounds.Height / 2 - center) > 1) return Failure(control.Name);
            }
            var speaker = MuteToggle.TransformToVisual(TransportPanel).TransformPoint(new());
            var slider = VolumeSlider.TransformToVisual(TransportPanel).TransformPoint(new());
            var list = QueueButton.TransformToVisual(TransportPanel).TransformPoint(new());
            bool fits = speaker.X + MuteToggle.ActualWidth <= slider.X
                && slider.X + VolumeSlider.ActualWidth <= list.X
                && Math.Abs(list.X + QueueButton.ActualWidth - TransportPanel.ActualWidth) < 1;
            return fits || Failure("speaker-slider-queue-spacing");
        }
        try
        {
            // Playback is already paused by the preceding fixture; never emit audio while checking mute.
            verify("player_controls_fixture_paused", music.Player.PlaybackSession.PlaybackState == global::Windows.Media.Playback.MediaPlaybackState.Paused);
            PlaybackInfoButton.Flyout.ShowAt(PlaybackInfoButton); await Task.Delay(80);
            verify("player_status_popover_retains_live_status_and_queue", PlaybackInfoStatus.IsLoaded && PlaybackInfoQueue.IsLoaded
                && PlaybackInfoStatus.Text == PlaybackStatus.Text && PlaybackInfoStatus.Text.Length > 0
                && PlaybackInfoQueue.Text == QueueStatus.Text && PlaybackInfoQueue.Text.Length > 0);
            PlaybackInfoButton.Flyout.Hide(); await Task.Delay(30);
            foreach (int width in new[] { 1360, 980, 820 })
            {
                await CloseNowPlayingAsync();
                AppWindow.Resize(new global::Windows.Graphics.SizeInt32(width, 680));
                verify("player_controls_same_row_main_" + width, await WaitForPlayerFramesAsync() && ControlsFit() && PlayerBar.Visibility == Visibility.Visible);
                var center = PlayPauseButton.TransformToVisual(Root).TransformPoint(new(PlayPauseButton.ActualWidth / 2, 0)).X;
                verify("player_main_centered_compact_footer_" + width, Math.Abs(center - Root.ActualWidth / 2) <= 1
                    && PlayerBar.ActualHeight <= 106 && NowCoverButton.ActualHeight >= 48 && PlaybackInfoButton.IsLoaded);
                var time = PlaybackTime.TransformToVisual(TransportPanel).TransformPoint(new());
                var progress = SeekSlider.TransformToVisual(TransportPanel).TransformPoint(new());
                var duration = PlaybackDuration.TransformToVisual(TransportPanel).TransformPoint(new());
                verify("player_progress_time_labels_flank_slider_" + width, time.X + PlaybackTime.ActualWidth <= progress.X
                    && progress.X + SeekSlider.ActualWidth <= duration.X
                    && Math.Abs(time.Y + PlaybackTime.ActualHeight / 2 - duration.Y - PlaybackDuration.ActualHeight / 2) <= 1);
                var sidebarRight = NewPlaylistNavigation.TransformToVisual(SidebarScroll).TransformPoint(new(NewPlaylistNavigation.ActualWidth, 0)).X;
                verify("sidebar_scroll_gutter_keeps_add_button_clear_" + width, SidebarScroll.ActualWidth - sidebarRight >= 19
                    && SidebarScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled);
                await OpenNowPlayingAsync();
                verify("player_controls_same_row_lyrics_" + width, await WaitForPlayerFramesAsync() && ControlsFit() && PlayerBar.Visibility == Visibility.Collapsed
                    && ReferenceEquals(FullTransportHost.Content, TransportPanel));
                verify("lyrics_player_scrollbar_cannot_cover_queue_button_" + width,
                    FullPlayerColumn.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden
                    && FullPlayerColumn.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                    && QueueButton.IsLoaded && QueueButton.ActualWidth > 0
                    && FindSongVisual<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(FullPlayerColumn,
                        bar => bar.Orientation == Orientation.Vertical && bar.Visibility == Visibility.Visible && bar.ActualWidth > 0) is null);
            }
            foreach (var item in new[] { SequentialModeItem, RepeatAllModeItem, RepeatOneModeItem, ShuffleModeItem })
            {
                QueueModeButton.Flyout.ShowAt(QueueModeButton); await Task.Delay(30);
                ((IToggleProvider)new ToggleMenuFlyoutItemAutomationPeer(item).GetPattern(PatternInterface.Toggle)).Toggle();
                verify("player_mode_menu_" + item.Tag, queue.Mode.ToString() == (string)item.Tag
                    && new[] { SequentialModeItem, RepeatAllModeItem, RepeatOneModeItem, ShuffleModeItem }.Count(option => option.IsChecked) == 1
                    && item.IsChecked && AutomationProperties.GetName(QueueModeButton).Contains(item.Text)
                    && QueueModeButton.Content is FontIcon { Glyph.Length: > 0 });
                QueueModeButton.Flyout.Hide(); await Task.Delay(30);
            }
            music.Player.IsMuted = false; VolumeSlider.Value = 37; UpdateVolumeControl();
            var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(MuteToggle).GetPattern(PatternInterface.Toggle);
            toggle.Toggle();
            verify("player_speaker_mutes_without_losing_volume", music.Player.IsMuted && VolumeIcon.Glyph == "\uE74F"
                && Math.Abs(music.Player.Volume - .37) < .001 && VolumeSlider.Value == 37 && AutomationProperties.GetName(MuteToggle) == "取消静音");
            toggle.Toggle();
            verify("player_speaker_unmutes_at_previous_volume", !music.Player.IsMuted && VolumeIcon.Glyph == "\uE767"
                && Math.Abs(music.Player.Volume - .37) < .001 && AutomationProperties.GetName(MuteToggle) == "静音");
            VolumeSlider.Value = 0;
            verify("player_zero_volume_uses_silent_icon", !music.Player.IsMuted && VolumeIcon.Glyph == "\uE74F");
            VolumeSlider.Value = 37; toggle.Toggle();
            await CloseNowPlayingAsync(); await OpenNowPlayingAsync();
            verify("player_reparented_controls_rendered_before_input", await WaitForPlayerFramesAsync() && ControlsFit());
            verify("player_page_switch_retains_mode_volume_device_and_audio", queue.Mode == QueueMode.Shuffle && music.Player.IsMuted
                && VolumeSlider.Value == 37 && MuteToggle.IsChecked == true && ReferenceEquals(music.Player.Source, audio)
                && music.Player.AudioDevice?.Id == device?.Id);
            var outputOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OutputOpened(object? sender, object args) => outputOpened.TrySetResult();
            OutputDeviceButton.Flyout.Opened += OutputOpened;
            try
            {
                ((IInvokeProvider)new ButtonAutomationPeer(OutputDeviceButton).GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.WhenAny(outputOpened.Task, Task.Delay(1500)); Root.UpdateLayout();
            }
            finally { OutputDeviceButton.Flyout.Opened -= OutputOpened; }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "player-output-diagnostic.json"),
                System.Text.Json.JsonSerializer.Serialize(new { Loaded = OutputDevices.IsLoaded, Enabled = OutputDeviceButton.IsEnabled,
                    Opened = outputOpened.Task.IsCompleted, Open = OutputDeviceButton.Flyout.IsOpen,
                    Width = OutputDevices.ActualWidth, Height = OutputDevices.ActualHeight, SameRoot = OutputDevices.XamlRoot == Root.XamlRoot,
                    ContentType = OutputDeviceButton.Content?.GetType().Name, AccessibleName = AutomationProperties.GetName(OutputDeviceButton) }));
            verify("player_output_icon_opens_device_picker", outputOpened.Task.IsCompleted && OutputDeviceButton.Flyout.IsOpen
                && OutputDevices.ActualWidth > 0 && OutputDevices.ActualHeight > 0 && OutputDevices.XamlRoot == Root.XamlRoot && OutputDeviceButton.Content is FontIcon
                && OutputDevices.SelectedItem is OutputOption selectedOutput
                && AutomationProperties.GetName(OutputDeviceButton) == "音频输出设备：" + selectedOutput.Name);
            OutputDeviceButton.Flyout.Hide(); await Task.Delay(50);
            ((IInvokeProvider)new ButtonAutomationPeer(QueueButton).GetPattern(PatternInterface.Invoke)).Invoke(); await Task.Delay(50);
            verify("player_rightmost_queue_icon_opens_drawer", queuePage is not null && QueueOverlay.Visibility == Visibility.Visible
                && QueuePane.HorizontalAlignment == HorizontalAlignment.Right && QueueButton.Content is FontIcon);
            await CloseQueuePageAsync();
        }
        finally
        {
            QueueModeButton.Flyout.Hide(); OutputDeviceButton.Flyout.Hide(); CloseQueuePage(force: true);
            queue.Mode = mode; UpdateQueueModeControl();
            VolumeSlider.Value = volume * 100; music.Player.IsMuted = muted; UpdateVolumeControl();
            AppWindow.Resize(size); await Task.Delay(100); UpdateAdaptiveLayout(Root.ActualWidth);
        }
    }
}
#endif
