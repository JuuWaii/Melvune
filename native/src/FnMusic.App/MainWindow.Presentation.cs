using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI.ViewManagement;

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
#if DEBUG
    private Func<Task>? localArtworkBeforeCommit;
    private Func<Task>? lyricsBeforeCommit;
#endif
    private void InitializePresentation()
    {
        InitializeThemeChoice();
        ApplySystemTheme();
        systemTheme.ColorValuesChanged += SystemThemeChanged;
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
    private async void SidebarFavorites_Click(object sender, RoutedEventArgs e) { ShowBrowsePanel(); await ShowFavoritesAsync(); }
    private async void SidebarAlbums_Click(object sender, RoutedEventArgs e) { ShowBrowsePanel(); await SwitchCollectionAsync(CollectionKind.Album); }
    private async void SidebarArtists_Click(object sender, RoutedEventArgs e) { ShowBrowsePanel(); await SwitchCollectionAsync(CollectionKind.Artist); }
    private void ShowBrowsePanel() { SettingsPanel.Visibility = Visibility.Collapsed; LibraryPanel.Visibility = Visibility.Visible; }
    private async void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        ShowBrowsePanel(); LyricsPanel.Visibility = LyricsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        UpdateAdaptiveLayout(Root.ActualWidth);
        if (LyricsPanel.Visibility == Visibility.Visible && lyricsNeedsRetry && queue.Current is { } track
            && TrackSupports(track, MusicSourceCapabilities.Lyrics)) await LoadLyricsAsync(track);
        if (closed) return;
        if (LyricsPanel.Visibility == Visibility.Visible && activeLyric >= 0) LyricsList.ScrollIntoView(currentLyrics.Lines[activeLyric]);
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
        LyricsStatus.Text = "播放歌曲后显示歌词。"; NowCover.DataContext = null; NowCover.Source = null; NowAlbum.Text = "";
    }
    private async Task LoadLyricsAsync(MusicTrack track)
    {
        if (closed) return;
        ClearPresentation(); NowCover.DataContext = track; NowAlbum.Text = track.Album;
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
        activeLyric = index; LyricsList.SelectedIndex = index;
        if (index >= 0 && LyricsPanel.Visibility == Visibility.Visible) LyricsList.ScrollIntoView(currentLyrics.Lines[index]);
    }
    private void Lyric_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!currentLyrics.IsTimed || e.ClickedItem is not LyricLine line || !mediaReady || !music.Player.PlaybackSession.CanSeek) return;
        pendingSeek = Math.Clamp(line.Seconds, 0, music.Player.PlaybackSession.NaturalDuration.TotalSeconds); CommitSeek();
    }
}
