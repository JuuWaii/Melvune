#if DEBUG
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private async Task VerifyFullscreenPresentationAsync(Action<string, bool> verify)
    {
        await CloseNowPlayingAsync();
        string previousSearch = SearchInput.Text;
        int previousPage = page;
        var previousSelection = (TrackList.SelectedItem as FnMusic.Core.MusicTrack)?.Reference;
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
            && ReferenceEquals(FullOptionsHost.Content, PlayerOptionsPanel) && ReferenceEquals(FullOutputHost.Content, OutputDeviceButton)
            && !PlayerLayout.Children.Contains(TransportPanel) && PlayerBar.Visibility == Visibility.Collapsed
            && Microsoft.UI.Xaml.Controls.Grid.GetRowSpan(LyricsPanel) == 2);
        verify("now_playing_uses_current_presentation_and_same_audio", FullNowTitle.Text == ((FnMusic.Core.MusicTrack)NowCover.DataContext).Title
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
            && PlayerLayout.Children.Contains(TransportPanel) && PlayerLayout.Children.Contains(PlayerOptionsPanel)
            && PlayerLayout.Children.Contains(OutputDeviceButton) && FullTransportHost.Content is null && FullOptionsHost.Content is null
            && FullOutputHost.Content is null && PlayerBar.Visibility == Visibility.Visible);
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
        if (previousSelection is { } reference && TrackList.ItemsSource is IEnumerable<FnMusic.Core.MusicTrack> restored)
            TrackList.SelectedItem = restored.FirstOrDefault(track => track.Reference == reference);
        await OpenNowPlayingAsync();
    }
}
#endif
