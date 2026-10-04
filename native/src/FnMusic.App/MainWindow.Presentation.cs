using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private readonly UISettings systemTheme = new();
    private readonly SemaphoreSlim coverSlots = new(4);
    private readonly Dictionary<string, BitmapImage> coverCache = [];
    private CancellationTokenSource lyricsWork = new();
    private Lyrics currentLyrics = Lyrics.Empty;
    private int activeLyric = -1;
    private bool lyricsNeedsRetry;
    private Control? lyricsPreviousFocus;
    private ScrollViewer? lyricsPreviousScroll;
    private ListView? lyricsPreviousList;
    private object? lyricsPreviousItems;
    private double lyricsPreviousHorizontalOffset, lyricsPreviousVerticalOffset;
    private Visibility lyricsSidebarVisibility, lyricsSettingsVisibility, lyricsLibraryVisibility;
    private Visibility lyricsPlaylistVisibility, lyricsHomeVisibility;
    private Task? nowPlayingExit;
    private int nowPlayingTransitionVersion;
    private bool normalContentWasEnabled;
    private double normalPlayerMinHeight;
#if DEBUG
    private Func<Task>? localArtworkBeforeCommit;
    private Func<Task>? lyricsBeforeCommit;
#endif
    private void InitializePresentation()
    {
        InitializeThemeChoice();
        ApplySystemTheme();
        systemTheme.ColorValuesChanged += SystemThemeChanged;
        systemTheme.AnimationsEnabledChanged += SystemMotionChanged;
        Root.SizeChanged += (_, e) => UpdateAdaptiveLayout(e.NewSize.Width);
        Root.ActualThemeChanged += (_, _) => { UpdateAdaptiveLayout(Root.ActualWidth); UpdateTitleBarTheme(); };
        UpdateTitleBarTheme();
    }
    private void UpdateTitleBarTheme()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var background = global::Windows.UI.Color.FromArgb(255, dark ? (byte)32 : (byte)243, dark ? (byte)32 : (byte)243, dark ? (byte)32 : (byte)243);
        var foreground = dark ? global::Microsoft.UI.Colors.White : global::Microsoft.UI.Colors.Black;
        var bar = AppWindow.TitleBar;
        bar.BackgroundColor = bar.ButtonBackgroundColor = bar.InactiveBackgroundColor = bar.ButtonInactiveBackgroundColor = background;
        bar.ForegroundColor = bar.ButtonForegroundColor = bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(255, 128, 128, 128);
        bar.ButtonHoverForegroundColor = global::Microsoft.UI.Colors.White;
    }
    private void SystemThemeChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!closed) ApplySystemTheme(); });
    private void ApplySystemTheme()
    {
        var foreground = systemTheme.GetColorValue(UIColorType.Foreground);
        Root.RequestedTheme = themePreference switch
        {
            FnMusic.Infrastructure.ThemePreference.Light => ElementTheme.Light,
            FnMusic.Infrastructure.ThemePreference.Dark => ElementTheme.Dark,
            _ => foreground.R + foreground.G + foreground.B > 384 ? ElementTheme.Dark : ElementTheme.Light
        };
    }
    private async void SidebarFavorites_Click(object sender, RoutedEventArgs e) { if (await ShowBrowsePanelAsync()) await ShowFavoritesAsync(); }
    private async void SidebarAlbums_Click(object sender, RoutedEventArgs e) { if (await ShowBrowsePanelAsync()) await SwitchCollectionAsync(CollectionKind.Album); }
    private async void SidebarArtists_Click(object sender, RoutedEventArgs e) { if (await ShowBrowsePanelAsync()) await SwitchCollectionAsync(CollectionKind.Artist); }
    private async Task<bool> ShowBrowsePanelAsync() => await NavigateMainContentAsync(LibraryPanel);
    private async void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        if (LyricsPanel.Visibility == Visibility.Visible) { await CloseNowPlayingAsync(); return; }
        await OpenNowPlayingAsync();
    }
    private async Task OpenNowPlayingAsync()
    {
        CloseTrackContextMenu();
        if (nowPlayingExit is { IsCompleted: false } exit) await exit;
        if (closed || LyricsPanel.Visibility == Visibility.Visible) return;
        int transition = ++nowPlayingTransitionVersion;
        lyricsPreviousFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        lyricsPreviousList = PlaylistPanel.Visibility == Visibility.Visible ? activePlaylistPage?.List : SettingsPanel.Visibility == Visibility.Visible ? null
            : CollectionList.Visibility == Visibility.Visible ? CollectionList : TrackList;
        lyricsPreviousItems = lyricsPreviousList?.ItemsSource;
        lyricsPreviousScroll = lyricsPreviousList is null ? SettingsPanel : FindPresentationScroll(lyricsPreviousList);
        lyricsPreviousHorizontalOffset = lyricsPreviousScroll?.HorizontalOffset ?? 0;
        lyricsPreviousVerticalOffset = lyricsPreviousScroll?.VerticalOffset ?? 0;
        lyricsSidebarVisibility = SidebarHost.Visibility;
        lyricsSettingsVisibility = SettingsPanel.Visibility;
        lyricsLibraryVisibility = LibraryPanel.Visibility;
        lyricsPlaylistVisibility = PlaylistPanel.Visibility;
        lyricsHomeVisibility = HomePanel.Visibility;
        normalContentWasEnabled = NormalContent.IsEnabled;
        normalPlayerMinHeight = PlayerBar.MinHeight;
        PlayerBar.MinHeight = PlayerBar.ActualHeight;
        NormalContent.IsEnabled = false;
        PlayerLayout.Children.Remove(TransportPanel);
        PlayerLayout.Children.Remove(PlayerOptionsPanel);
        PlayerLayout.Children.Remove(OutputDeviceButton);
        FullTransportHost.Content = TransportPanel;
        FullOptionsHost.Content = PlayerOptionsPanel;
        FullOutputHost.Content = OutputDeviceButton;
        PlaySelectedButton.Visibility = Visibility.Collapsed;
        LyricsPanel.Visibility = Visibility.Visible;
        UpdateAdaptiveLayout(Root.ActualWidth);
        NowPlayingBack.Focus(FocusState.Programmatic);
        await AnimateUiAsync(LyricsPanel, Math.Max(1, Root.ActualHeight), 0, 320);
        if (closed || transition != nowPlayingTransitionVersion) return;
        SidebarHost.Visibility = SettingsPanel.Visibility = LibraryPanel.Visibility = PlayerBar.Visibility = Visibility.Collapsed;
        PlaylistPanel.Visibility = HomePanel.Visibility = Visibility.Collapsed;
        if (LyricsPanel.Visibility == Visibility.Visible && lyricsNeedsRetry && queue.Current is { } track
            && TrackSupports(track, MusicSourceCapabilities.Lyrics)) await LoadLyricsAsync(track);
        if (closed) return;
        if (LyricsPanel.Visibility == Visibility.Visible && activeLyric >= 0) CenterActiveLyric();
    }
    private Task CloseNowPlayingAsync() => nowPlayingExit is { IsCompleted: false } pending ? pending : nowPlayingExit = CloseNowPlayingCoreAsync();
    private async Task CloseNowPlayingCoreAsync()
    {
        if (closed || LyricsPanel.Visibility != Visibility.Visible) return;
        ++nowPlayingTransitionVersion;
        SidebarHost.Visibility = lyricsSidebarVisibility;
        SettingsPanel.Visibility = lyricsSettingsVisibility;
        LibraryPanel.Visibility = lyricsLibraryVisibility;
        PlaylistPanel.Visibility = lyricsPlaylistVisibility;
        HomePanel.Visibility = lyricsHomeVisibility;
        PlayerBar.Visibility = Visibility.Visible;
        double from = FindUiMotionTransform(LyricsPanel)?.TranslateY ?? 0;
        await AnimateUiAsync(LyricsPanel, from, Math.Max(1, Root.ActualHeight), 220);
        if (closed) return;
        FullTransportHost.Content = FullOptionsHost.Content = FullOutputHost.Content = null;
        PlayerLayout.Children.Add(TransportPanel);
        PlayerLayout.Children.Add(PlayerOptionsPanel);
        PlayerLayout.Children.Add(OutputDeviceButton);
        PlaySelectedButton.Visibility = PlayerBar.Visibility = Visibility.Visible;
        PlayerBar.MinHeight = normalPlayerMinHeight;
        NormalContent.IsEnabled = normalContentWasEnabled;
        LyricsPanel.Visibility = Visibility.Collapsed;
        if (FindUiMotionTransform(LyricsPanel) is { } transform) transform.TranslateY = 0;
        SidebarHost.Visibility = lyricsSidebarVisibility;
        SettingsPanel.Visibility = lyricsSettingsVisibility;
        LibraryPanel.Visibility = lyricsLibraryVisibility;
        PlaylistPanel.Visibility = lyricsPlaylistVisibility;
        HomePanel.Visibility = lyricsHomeVisibility;
        UpdateAdaptiveLayout(Root.ActualWidth);
        Root.UpdateLayout();
        if (lyricsPreviousScroll is { } scroll && (lyricsPreviousList is null || ReferenceEquals(lyricsPreviousItems, lyricsPreviousList.ItemsSource)))
            scroll.ChangeView(lyricsPreviousHorizontalOffset, lyricsPreviousVerticalOffset, null, true);
        if (lyricsPreviousFocus is { IsLoaded: true, IsEnabled: true } control) control.Focus(FocusState.Programmatic);
        else NowCoverButton.Focus(FocusState.Programmatic);
        lyricsPreviousFocus = null;
        lyricsPreviousScroll = null; lyricsPreviousList = null; lyricsPreviousItems = null;
    }
    private static ScrollViewer? FindPresentationScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindPresentationScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
    private async void NowPlayingBack_Click(object sender, RoutedEventArgs e) => await CloseNowPlayingAsync();
    private async void NowPlayingEscape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (LyricsPanel.Visibility != Visibility.Visible || themedDialogOpen) return;
        args.Handled = true; await CloseNowPlayingAsync();
    }

    private async void Artwork_Loaded(object sender, RoutedEventArgs e) { if (sender is Image image) await LoadArtworkAsync(image); }
    private async void Artwork_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) { if (sender is Image { IsLoaded: true } image) await LoadArtworkAsync(image); }
    private void Artwork_Unloaded(object sender, RoutedEventArgs e)
    { if (sender is Image image && image.Tag is CancellationTokenSource work) { work.Cancel(); image.Tag = null; } }
    private async Task LoadArtworkAsync(Image image)
    {
        if (closed) return;
        if (image.Tag is CancellationTokenSource old) old.Cancel();
        image.Tag = null;
        image.Source = null;
        object context = image.DataContext;
        if (context is MusicTrack unsupported && !TrackSupports(unsupported, MusicSourceCapabilities.Artwork)) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(localLifetime.Token);
        image.Tag = request; request.CancelAfter(TimeSpan.FromSeconds(15)); var ct = request.Token;
        var api = ViewModel.AuthenticatedClient;
        var source = context is MusicTrack localContext ? localSources.GetValueOrDefault(localContext.SourceInstanceId) : null;
        string? coverId = context is MusicTrack track ? track.CoverId : context is MusicCollection album ? album.CoverId : null;
        string? key = context is MusicTrack song && source?.Source.Id == song.SourceInstanceId ? "local:" + song.Reference
            : api is not null && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.Artwork)
                && !string.IsNullOrEmpty(coverId) && (context is not MusicTrack remote || remote.SourceInstanceId == api.SourceInstanceId)
                ? api.SourceInstanceId + ":" + coverId : null;
        if (key is null) { image.Tag = null; return; }
        try
        {
            if (coverCache.TryGetValue(key, out var cached)) { image.Source = cached; return; }
            await coverSlots.WaitAsync(ct);
            try
            {
                BitmapImage? bitmap = null;
                if (context is MusicTrack local && source?.Source.Id == local.SourceInstanceId)
                {
                    await foreach (var candidate in source.GetSidecarCoversAsync(local.Reference, ct))
                    {
                        try { bitmap = await DecodeArtworkAsync(candidate, ct); break; }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception) { /* Invalid sidecars must not hide later or embedded covers. */ }
                    }
                    if (bitmap is null)
                    {
                        var thumbnail = await Services.LocalArtworkReader.ReadThumbnailAsync(source.GetFilePath(local.Reference), ct);
                        if (thumbnail is null) return;
                        bitmap = await DecodeArtworkAsync(thumbnail, ct);
                    }
                }
                else
                {
                    var bytes = await api!.GetCoverAsync(coverId!, ct);
                    if (bytes is null || bytes.Length == 0) return;
                    bitmap = await DecodeArtworkAsync(bytes, ct);
                }
#if DEBUG
                if (source is not null && localArtworkBeforeCommit is { } beforeCommit) await beforeCommit();
#endif
                if (ct.IsCancellationRequested || closed || !ReferenceEquals(image.DataContext, context)) return;
                if (source is not null)
                {
                    if (!ReferenceEquals(localSources.GetValueOrDefault(source.Source.Id), source)) return;
                }
                else
                    if (!ReferenceEquals(api, ViewModel.AuthenticatedClient)) return;
                if (coverCache.Count >= 96) coverCache.Clear();
                coverCache[key] = bitmap; image.Source = bitmap;
            }
            finally { coverSlots.Release(); }
        }
        catch (Exception) { /* 封面失败保留占位，不影响播放或暴露地址。 */ }
        finally { if (ReferenceEquals(image.Tag, request)) image.Tag = null; }
    }

    private static async Task<BitmapImage> DecodeArtworkAsync(byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bitmap = new BitmapImage { DecodePixelWidth = 256 };
        using var input = new MemoryStream(bytes);
        await bitmap.SetSourceAsync(input.AsRandomAccessStream()).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        return bitmap;
    }

    private void ClearPresentation()
    {
        lyricsWork.Cancel(); lyricsWork.Dispose(); lyricsWork = new();
        currentLyrics = Lyrics.Empty; activeLyric = -1; LyricsList.ItemsSource = null;
        lyricsNeedsRetry = false;
        LyricsStatus.Text = "播放歌曲后显示歌词。"; NowCover.DataContext = FullNowCover.DataContext = null; NowCover.Source = null; NowAlbum.Text = "";
        FullNowCover.Source = null;
        FullNowTitle.Text = "尚未播放歌曲"; FullNowArtist.Text = "";
    }
    private async Task LoadLyricsAsync(MusicTrack track)
    {
        if (closed) return;
        ClearPresentation(); NowCover.DataContext = FullNowCover.DataContext = track; NowAlbum.Text = track.Album;
        FullNowTitle.Text = track.Title; FullNowArtist.Text = string.Join(" · ", new[] { track.Artist, track.Album }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!TrackSupports(track, MusicSourceCapabilities.Lyrics))
        { lyricsNeedsRetry = true; LyricsStatus.Text = "当前歌曲来源未连接或不支持歌词。"; return; }
        var ct = lyricsWork.Token; LyricsStatus.Text = "正在加载歌词…";
        var source = localSources.GetValueOrDefault(track.SourceInstanceId);
        var api = ViewModel.AuthenticatedClient;
        bool SourceIsCurrent() => TrackSupports(track, MusicSourceCapabilities.Lyrics) && (source is not null
            ? ReferenceEquals(source, localSources.GetValueOrDefault(track.SourceInstanceId))
            : api is not null && track.SourceInstanceId == api.SourceInstanceId && ReferenceEquals(api, ViewModel.AuthenticatedClient)
#if DEBUG
                || (IsSyntheticPreview && track.SourceInstanceId == Guid.Empty)
#endif
        );
        try
        {
            Lyrics result;
#if DEBUG
            if (IsSyntheticPreview && track.SourceInstanceId == Guid.Empty)
                result = Lyrics.Parse("[00:00.00]合成歌词验证\n[00:10.00]播放进度同步\n[00:20.00]点击歌词可跳转");
            else
#endif
            if (source is not null) result = await source.GetLyricsAsync(track.Reference, ct);
            else if (api is not null && api.SourceInstanceId == track.SourceInstanceId) result = await api.GetLyricsAsync(track.Reference, ct);
            else result = Lyrics.Empty;
#if DEBUG
            if (lyricsBeforeCommit is { } beforeCommit) await beforeCommit();
#endif
            if (ct.IsCancellationRequested || closed) return;
            if (!SourceIsCurrent()) { lyricsNeedsRetry = true; LyricsStatus.Text = "歌曲来源已变化或不支持歌词，打开歌词可重试。"; return; }
            currentLyrics = result; LyricsList.ItemsSource = result.Lines;
            LyricsStatus.Text = result.Lines.Count == 0 ? "暂无歌词。本地音乐可放置同名 .lrc 文件。" : result.IsTimed ? "随播放高亮 · 点击歌词跳转" : "纯文本歌词（无时间轴）";
            UpdateLyricsPosition();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!closed && !ct.IsCancellationRequested)
            {
                lyricsNeedsRetry = true;
                LyricsStatus.Text = SourceIsCurrent() ? "歌词加载失败，打开歌词可重试。" : "歌曲来源已变化或不支持歌词，打开歌词可重试。";
            }
        }
    }
    private void UpdateLyricsPosition()
    {
        if (!mediaReady) return;
        int index = currentLyrics.FindLine(music.Player.PlaybackSession.Position.TotalSeconds);
        if (index == activeLyric) return;
        int previous = activeLyric;
        activeLyric = index; LyricsList.SelectedIndex = index;
        if (previous >= 0 && LyricsList.ContainerFromIndex(previous) is ListViewItem old) ApplyLyricEmphasis(old, previous);
        if (index >= 0 && LyricsList.ContainerFromIndex(index) is ListViewItem current) ApplyLyricEmphasis(current, index);
        if (index >= 0 && LyricsPanel.Visibility == Visibility.Visible) CenterActiveLyric();
    }
    private void CenterActiveLyric()
    {
        int index = activeLyric;
        if (index < 0 || index >= currentLyrics.Lines.Count) return;
        LyricsList.ScrollIntoView(currentLyrics.Lines[index]);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || LyricsPanel.Visibility != Visibility.Visible || activeLyric != index) return;
            LyricsList.UpdateLayout();
            if (LyricsList.ContainerFromIndex(index) is not ListViewItem { ActualHeight: > 0 } row
                || FindPresentationScroll(LyricsList) is not { } scroll) return;
            double top = row.TransformToVisual(LyricsList).TransformPoint(new global::Windows.Foundation.Point()).Y;
            double target = scroll.VerticalOffset + top - (scroll.ViewportHeight - row.ActualHeight) / 2;
            scroll.ChangeView(null, Math.Clamp(target, 0, scroll.ScrollableHeight), null, true);
        });
    }
    private void Lyric_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Phase == 0) args.RegisterUpdateCallback(1, Lyric_ContainerContentChanging);
        ApplyLyricEmphasis(args.ItemContainer, args.ItemIndex);
    }
    private void ApplyLyricEmphasis(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem container, int index)
    {
        if (container.ContentTemplateRoot is not TextBlock text) return;
        bool current = currentLyrics.IsTimed && index == activeLyric;
        text.Opacity = !currentLyrics.IsTimed || current ? 1 : .55;
        text.FontWeight = current ? global::Microsoft.UI.Text.FontWeights.SemiBold : global::Microsoft.UI.Text.FontWeights.Normal;
    }
    private void Lyric_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!currentLyrics.IsTimed || e.ClickedItem is not LyricLine line || !mediaReady || !music.Player.PlaybackSession.CanSeek) return;
        pendingSeek = Math.Clamp(line.Seconds, 0, music.Player.PlaybackSession.NaturalDuration.TotalSeconds); CommitSeek();
    }
}
