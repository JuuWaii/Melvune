using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private bool localView, localImportBusy;
    private LocalMusicSource? localSource;
    private CancellationTokenSource? localScan;
    private readonly CancellationTokenSource localLifetime = new();
    private readonly LocalMusicSettingsStore localSettingsStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FnMusic.Native"));

    private async void LocalMusic_Click(object sender, RoutedEventArgs e)
    { await ShowLocalMusicAsync(); }
    private async Task ShowLocalMusicAsync()
    {
        localView = true; favoritesView = false; parentArtist = null; collectionKind = null; collection = null;
        SearchInput.Text = searchQuery = ""; UpdateCollectionView();
        SettingsPanel.Visibility = Visibility.Collapsed; LibraryPanel.Visibility = Visibility.Visible;
        await LoadPageAsync(1);
    }

    private async void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        if (localImportBusy) return;
        localImportBusy = true;
        try
        {
            var input = new TextBox { Header = "音乐文件夹完整路径", PlaceholderText = @"例如 D:\Music", Text = localSource?.Settings.Directory ?? "" };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(input);
            var browse = new Button { Content = "浏览文件夹…" };
            browse.Click += async (_, _) =>
            {
                browse.IsEnabled = false;
                try
                {
                    var picker = new global::Windows.Storage.Pickers.FolderPicker();
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    var folder = await picker.PickSingleFolderAsync();
                    if (folder is not null) input.Text = folder.Path;
                }
                catch (Exception) { input.Header = "无法打开选择器，请直接输入文件夹完整路径"; }
                finally { browse.IsEnabled = true; }
            };
            panel.Children.Add(browse);
            panel.Children.Add(new TextBlock { Text = "包含子文件夹，最多 10000 首；跳过链接和无权限目录。读取标题、歌手与时长，无法读取时保留文件名。", TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "导入本地音乐", Content = panel, PrimaryButtonText = "导入", CloseButtonText = "取消" };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed) return;
            if (!Path.IsPathFullyQualified(input.Text.Trim())) throw new ArgumentException("Absolute directory required");
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.Text.Trim()));
            var saved = await localSettingsStore.LoadAsync(localLifetime.Token);
            Guid id = saved is not null && string.Equals(Path.TrimEndingDirectorySeparator(saved.Directory), path, StringComparison.OrdinalIgnoreCase)
                ? saved.SourceId : Guid.NewGuid();
            SettingsPanel.Visibility = Visibility.Collapsed; LibraryPanel.Visibility = Visibility.Visible;
            PlaybackStatus.Text = "正在扫描本地音乐…";
            var scanned = await ScanLocalAsync(new(id, path), localLifetime.Token);
            await localSettingsStore.SaveAsync(scanned.Settings, localLifetime.Token);
            if (closed) return;
            libraryWork.Cancel();
            ReplaceLocalSource(scanned);
            await ShowLocalMusicAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!closed)
            {
                SettingsPanel.Visibility = Visibility.Collapsed; LibraryPanel.Visibility = Visibility.Visible;
                PlaybackStatus.Text = "导入失败，请检查目录是否可访问、是否包含链接或超过 10000 首；原音乐库保持不变。";
            }
        }
        finally { localImportBusy = false; }
    }

    private void ReplaceLocalSource(LocalMusicSource source)
    {
        coverCache.Clear();
        if (localSource is not null) ViewModel.PlaybackRouter.Unregister(localSource.Source.Id);
        ViewModel.PlaybackRouter.Register(source); localSource = source;
    }

    private async Task LoadLocalPageAsync(int requestedPage, bool rescan = false)
    {
        libraryWork.Cancel(); libraryWork.Dispose(); libraryWork = new();
        var ct = libraryWork.Token;
        TrackList.ItemsSource = null; PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        PageStatus.Text = "正在读取本地音乐…";
        try
        {
            if (localSource is null || rescan)
            {
                var saved = localSource?.Settings ?? await localSettingsStore.LoadAsync(ct);
                if (saved is null) { PageStatus.Text = "尚未导入本地音乐，请选择“导入文件夹”。"; return; }
                var source = await ScanLocalAsync(saved, ct);
                if (ct.IsCancellationRequested || closed) return;
                ReplaceLocalSource(source);
            }
            if (ct.IsCancellationRequested || closed) return;
            var result = localSource!.Tracks.Where(track => track.Title.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
                || track.Artist.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
            total = result.Length; page = Math.Clamp(requestedPage, 1, Math.Max(1, (total + 49) / 50));
            TrackList.ItemsSource = result.Skip((page - 1) * 50).Take(50).ToArray();
            TrackList.SelectedIndex = total > 0 ? 0 : -1;
            PageStatus.Text = $"本地音乐 · 第 {page} 页，共 {total} 首";
            PreviousPage.IsEnabled = page > 1; NextPage.IsEnabled = page * 50 < total;
        }
        catch (OperationCanceledException)
        { if (!closed && !ct.IsCancellationRequested) PageStatus.Text = "扫描已取消，点击本地音乐可返回原列表。"; }
        catch (Exception) { if (!closed && !ct.IsCancellationRequested) PageStatus.Text = "本地目录不可用，请检查磁盘或重新导入；已保存的目录配置仍保留。"; }
    }

    private void CancelLocalScan_Click(object sender, RoutedEventArgs e) => localScan?.Cancel();

    private async Task<LocalMusicSource> ScanLocalAsync(LocalMusicSettings settings, CancellationToken parent,
        Func<string, CancellationToken, Task<LocalMusicMetadata>>? reader = null)
    {
        localScan?.Cancel();
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(parent, localLifetime.Token);
        localScan = scan;
        CancelLocalScan.Visibility = Visibility.Visible;
        LocalScanStatus.Text = "正在扫描本地音乐…";
        var progress = new Progress<int>(count =>
        {
            if (!closed && ReferenceEquals(localScan, scan) && !scan.IsCancellationRequested)
                LocalScanStatus.Text = $"已读取 {count} 首，正在扫描…";
        });
        try
        {
            var result = await LocalMusicSource.ScanAsync(settings, scan.Token, reader ?? Services.LocalMusicMetadataReader.ReadAsync, progress);
            scan.Token.ThrowIfCancellationRequested();
            if (!closed && ReferenceEquals(localScan, scan)) LocalScanStatus.Text = $"扫描完成，共 {result.Tracks.Count} 首。";
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!closed && ReferenceEquals(localScan, scan)) LocalScanStatus.Text = "扫描已取消，原音乐库保留。";
            throw;
        }
        catch
        {
            if (!closed && ReferenceEquals(localScan, scan)) LocalScanStatus.Text = "扫描失败，原音乐库保留。";
            throw;
        }
        finally
        {
            if (ReferenceEquals(localScan, scan))
            {
                localScan = null;
                if (!closed) CancelLocalScan.Visibility = Visibility.Collapsed;
            }
        }
    }
}
