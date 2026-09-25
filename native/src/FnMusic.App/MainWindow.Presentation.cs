using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.Storage.FileProperties;
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
    private void InitializePresentation()
    {
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
        Root.RequestedTheme = foreground.R + foreground.G + foreground.B > 384 ? ElementTheme.Dark : ElementTheme.Light;
    }
    private async void SidebarFavorites_Click(object sender, RoutedEventArgs e) { ShowBrowsePanel(); await ShowFavoritesAsync(); }
    private async void SidebarAlbums_Click(object sender, RoutedEventArgs e) { ShowBrowsePanel(); await SwitchCollectionAsync(CollectionKind.Album); }
    private async void SidebarArtists_Click(object sender, RoutedEventArgs e) { ShowBrowsePanel(); await SwitchCollectionAsync(CollectionKind.Artist); }
    private void ShowBrowsePanel() { SettingsPanel.Visibility = Visibility.Collapsed; LibraryPanel.Visibility = Visibility.Visible; }
    private void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        ShowBrowsePanel(); LyricsPanel.Visibility = LyricsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        UpdateAdaptiveLayout(Root.ActualWidth);
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
        image.Source = null;
        object context = image.DataContext;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(localLifetime.Token);
        image.Tag = request; request.CancelAfter(TimeSpan.FromSeconds(15)); var ct = request.Token;
        var api = ViewModel.AuthenticatedClient;
        var source = localSource;
        string? coverId = context is MusicTrack track ? track.CoverId : context is MusicCollection album ? album.CoverId : null;
        string? key = context is MusicTrack song && source?.Source.Id == song.SourceInstanceId ? "local:" + song.Reference
            : api is not null && !string.IsNullOrEmpty(coverId) && (context is not MusicTrack remote || remote.SourceInstanceId == api.SourceInstanceId)
                ? api.SourceInstanceId + ":" + coverId : null;
        if (key is null) { image.Tag = null; return; }
        try
        {
            if (coverCache.TryGetValue(key, out var cached)) { image.Source = cached; return; }
            await coverSlots.WaitAsync(ct);
            try
            {
                byte[]? bytes;
                if (context is MusicTrack local && source?.Source.Id == local.SourceInstanceId)
                {
                    bytes = await source.GetSidecarCoverAsync(local.Reference, ct);
                    if (bytes is null)
                    {
                        var file = await StorageFile.GetFileFromPathAsync(source.GetFilePath(local.Reference)).AsTask(ct);
                        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 256, ThumbnailOptions.UseCurrentScale).AsTask(ct);
                        if (thumbnail is null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size > 4 * 1024 * 1024) return;
                        using var stream = thumbnail.AsStreamForRead(); using var memory = new MemoryStream();
                        await stream.CopyToAsync(memory, ct); bytes = memory.ToArray();
                    }
                }
                else bytes = await api!.GetCoverAsync(coverId!, ct);
                ct.ThrowIfCancellationRequested();
                if (bytes is null || bytes.Length == 0 || closed || !ReferenceEquals(image.DataContext, context)) return;
                var bitmap = new BitmapImage { DecodePixelWidth = 256 };
                using var input = new MemoryStream(bytes); await bitmap.SetSourceAsync(input.AsRandomAccessStream());
                if (ct.IsCancellationRequested || closed || !ReferenceEquals(image.DataContext, context)) return;
                if (context is not MusicTrack localTrack || localTrack.SourceInstanceId != source?.Source.Id)
                    if (!ReferenceEquals(api, ViewModel.AuthenticatedClient)) return;
                if (coverCache.Count >= 96) coverCache.Clear();
                coverCache[key] = bitmap; image.Source = bitmap;
            }
            finally { coverSlots.Release(); }
        }
        catch (Exception) { /* 封面失败保留占位，不影响播放或暴露地址。 */ }
        finally { if (ReferenceEquals(image.Tag, request)) image.Tag = null; }
    }

    private void ClearPresentation()
    {
        lyricsWork.Cancel(); lyricsWork.Dispose(); lyricsWork = new();
        currentLyrics = Lyrics.Empty; activeLyric = -1; LyricsList.ItemsSource = null;
        LyricsStatus.Text = "播放歌曲后显示歌词。"; NowCover.DataContext = null; NowCover.Source = null; NowAlbum.Text = "";
    }
    private async Task LoadLyricsAsync(MusicTrack track)
    {
        if (closed) return;
        ClearPresentation(); NowCover.DataContext = track; NowAlbum.Text = track.Album;
        var ct = lyricsWork.Token; LyricsStatus.Text = "正在加载歌词…";
        try
        {
            Lyrics result;
#if DEBUG
            if (IsSyntheticPreview && track.SourceInstanceId == Guid.Empty)
                result = Lyrics.Parse("[00:00.00]合成歌词验证\n[00:10.00]播放进度同步\n[00:20.00]点击歌词可跳转");
            else
#endif
            if (localSource?.Source.Id == track.SourceInstanceId) result = await localSource.GetLyricsAsync(track.Reference, ct);
            else if (ViewModel.AuthenticatedClient is { } api && api.SourceInstanceId == track.SourceInstanceId) result = await api.GetLyricsAsync(track.Reference, ct);
            else result = Lyrics.Empty;
            if (ct.IsCancellationRequested || closed) return;
            currentLyrics = result; LyricsList.ItemsSource = result.Lines;
            LyricsStatus.Text = result.Lines.Count == 0 ? "暂无歌词。本地音乐可放置同名 .lrc 文件。" : result.IsTimed ? "随播放高亮 · 点击歌词跳转" : "纯文本歌词（无时间轴）";
            UpdateLyricsPosition();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { if (!closed && !ct.IsCancellationRequested) LyricsStatus.Text = "歌词加载失败，可重新播放重试。"; }
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
