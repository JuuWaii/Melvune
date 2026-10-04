using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private bool localView, localImportBusy, localCatalogLoaded, updatingLibraryFilters;
    private LocalMusicSource? localSource;
    private readonly Dictionary<Guid, LocalMusicSource> localSources = [];
    private readonly HashSet<Guid> unavailableLibraries = [];
    private LocalLibraryCatalog localCatalog = LocalLibraryCatalog.Empty;
    private Guid? selectedLocalLibrary;
    private string? selectedLocalGroup;
    private sealed record LibraryFilter(Guid? Id, string Name);
    private CancellationTokenSource? localScan;
    private readonly CancellationTokenSource localLifetime = new();
    private readonly LocalLibraryStore localLibraryStore = new(ThemeSettingsDirectory());
#if DEBUG
    private Func<string, CancellationToken, Task<LocalMusicMetadata>>? localPageMetadataReader;
#endif

    private async void LocalMusic_Click(object sender, RoutedEventArgs e) => await ShowLocalMusicAsync();
    private async Task ShowLocalMusicAsync()
    {
        if (!await NavigateMainContentAsync(LibraryPanel)) return;
        localView = true; favoritesView = false; parentArtist = null; collectionKind = null; collection = null;
        SearchInput.Text = searchQuery = ""; UpdateCollectionView();
        await LoadPageAsync(1);
    }

    private async void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        if (localImportBusy) return;
        localImportBusy = true;
        try
        {
            var input = new TextBox { Header = "音乐文件夹完整路径", PlaceholderText = @"例如 D:\Music" };
            var name = new TextBox { Header = "库名称", MaxLength = 120, PlaceholderText = "留空使用文件夹名称" };
            var group = new TextBox { Header = "分组（可选）", MaxLength = 120 };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(input); panel.Children.Add(name); panel.Children.Add(group);
            var browse = new Button { Content = "浏览文件夹…" };
            browse.Click += async (_, _) => await PickLocalFolderAsync(input, browse);
            panel.Children.Add(browse);
            panel.Children.Add(new TextBlock { Text = "保留已有音乐库。包含子文件夹，每库最多 10000 首；跳过链接和无权限目录。", TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "导入本地音乐库", Content = panel, PrimaryButtonText = "导入", CloseButtonText = "取消" };
            if (await ShowThemedDialogAsync(dialog) != ContentDialogResult.Primary || closed) return;
            if (!Path.IsPathFullyQualified(input.Text.Trim())) throw new ArgumentException();
            string path = LocalLibraryStore.NormalizePath(input.Text.Trim());
            var saved = await localLibraryStore.LoadAsync(localLifetime.Token);
            var existing = saved.Libraries.FirstOrDefault(item => string.Equals(LocalLibraryStore.NormalizePath(item.Directory), path, StringComparison.OrdinalIgnoreCase));
            var definition = existing ?? new LocalLibrary(Guid.NewGuid(), path,
                string.IsNullOrWhiteSpace(name.Text) ? new DirectoryInfo(path).Name : name.Text.Trim(), group.Text.Trim());
            if (string.IsNullOrWhiteSpace(definition.Name)) definition = definition with { Name = "本地音乐" };
            if (!await NavigateMainContentAsync(LibraryPanel)) return;
            PlaybackStatus.Text = "正在扫描本地音乐…";
            var scanned = await ScanLocalAsync(definition.Settings, localLifetime.Token);
            var next = saved with { Libraries = existing is null ? saved.Libraries.Append(definition).ToArray() : saved.Libraries };
            var persisted = await localLibraryStore.SaveAsync(next, localLifetime.Token);
            if (closed) return;
            ApplyLocalCatalog(persisted);
            ReplaceLocalSource(scanned);
            selectedLocalGroup = null; selectedLocalLibrary = definition.SourceId;
            RenderLocalFilters();
            await ShowLocalMusicAsync();
            if (!closed && PlaybackStatus.Text == "正在扫描本地音乐…")
                PlaybackStatus.Text = $"导入完成，已保存音乐库，共 {scanned.Tracks.Count} 首。";
        }
        catch (OperationCanceledException) { }
        catch (LocalLibraryConflictException) { if (!closed) PlaybackStatus.Text = "音乐库已在其他窗口修改，请重新导入。"; }
        catch (Exception)
        {
            if (!closed)
            {
                PlaybackStatus.Text = "导入失败，请检查目录是否可访问、是否包含链接或超过 10000 首；原音乐库保持不变。";
            }
        }
        finally { localImportBusy = false; }
    }

    private async Task PickLocalFolderAsync(TextBox input, Button browse)
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
    }

    private void ReplaceLocalSource(LocalMusicSource source)
    {
        coverCache.Clear();
        if (localSources.ContainsKey(source.Source.Id)) ViewModel.PlaybackRouter.Unregister(source.Source.Id);
        ViewModel.PlaybackRouter.Register(source); localSources[source.Source.Id] = source; localSource = source;
        unavailableLibraries.Remove(source.Source.Id);
#if DEBUG
        // In-memory verification fixtures do not load or overwrite user catalogs.
        if (IsSyntheticPreview && !localCatalog.Libraries.Any(item => item.SourceId == source.Source.Id))
        {
            localCatalog = localCatalog with { Libraries = localCatalog.Libraries.Append(new LocalLibrary(source.Source.Id, source.Settings.Directory, "合成音乐库", "")).ToArray() };
            localCatalogLoaded = true;
        }
#endif
    }

    private void ApplyLocalCatalog(LocalLibraryCatalog catalog)
    {
        foreach (var definition in catalog.Libraries)
        {
            if (localSources.TryGetValue(definition.SourceId, out var cached) && !string.Equals(
                LocalLibraryStore.NormalizePath(cached.Settings.Directory), LocalLibraryStore.NormalizePath(definition.Directory), StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.PlaybackRouter.Unregister(definition.SourceId);
                localSources.Remove(definition.SourceId);
            }
        }
        foreach (var id in localSources.Keys.Where(id => !catalog.Libraries.Any(item => item.SourceId == id)).ToArray())
        {
            if (queue.Current?.SourceInstanceId == id) Stop_Click(this, new RoutedEventArgs());
            foreach (var track in queue.Tracks.Where(track => track.SourceInstanceId == id).ToArray()) queue.Remove(track.Reference);
            ViewModel.PlaybackRouter.Unregister(id); localSources.Remove(id); unavailableLibraries.Remove(id);
        }
        UpdateQueueStatus();
        localCatalog = catalog; localCatalogLoaded = true;
        if (!catalog.Libraries.Any(item => item.SourceId == selectedLocalLibrary)) selectedLocalLibrary = null;
        if (selectedLocalGroup is not null && !catalog.Libraries.Any(item => item.Group == selectedLocalGroup)) selectedLocalGroup = null;
        RenderLocalFilters(); coverCache.Clear();
    }

    private async Task RestoreLocalSourcesAsync(CancellationToken ct)
    {
        if (!localCatalogLoaded) ApplyLocalCatalog(await localLibraryStore.LoadAsync(ct));
        foreach (var definition in localCatalog.Libraries)
        {
            if (localSources.ContainsKey(definition.SourceId)) continue;
            try { ReplaceLocalSource(await ScanLocalAsync(definition.Settings, ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { unavailableLibraries.Add(definition.SourceId); }
        }
        RenderLocalFilters();
    }

    private IEnumerable<LocalLibrary> FilteredLocalLibraries() => localCatalog.Libraries.Where(item =>
        (selectedLocalLibrary is null || item.SourceId == selectedLocalLibrary) &&
        (selectedLocalGroup is null || item.Group == selectedLocalGroup));

    private void RenderLocalFilters()
    {
        updatingLibraryFilters = true;
        LocalGroupFilter.ItemsSource = new[] { "全部分组" }.Concat(localCatalog.Libraries.Select(item => item.Group).Distinct().Order().Select(group => group == "" ? "未分组" : group)).ToArray();
        LocalGroupFilter.SelectedIndex = 0;
        // Use indices to distinguish real groups named '全部分组' or '未分组'.
        var groups = localCatalog.Libraries.Select(item => item.Group).Distinct().Order().ToArray();
        if (selectedLocalGroup is not null) LocalGroupFilter.SelectedIndex = Array.IndexOf(groups, selectedLocalGroup) + 1;
        var options = new[] { new LibraryFilter(null, "全部音乐库") }.Concat(localCatalog.Libraries
            .Where(item => selectedLocalGroup is null || item.Group == selectedLocalGroup)
            .Select(item => new LibraryFilter(item.SourceId, item.Name + (unavailableLibraries.Contains(item.SourceId) ? "（不可用）" : "")))).ToArray();
        LocalLibraryFilter.ItemsSource = options;
        LocalLibraryFilter.SelectedItem = options.FirstOrDefault(item => item.Id == selectedLocalLibrary) ?? options[0];
        selectedLocalLibrary = ((LibraryFilter)LocalLibraryFilter.SelectedItem).Id;
        updatingLibraryFilters = false;
    }

    private async void LocalFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updatingLibraryFilters || !localView) return;
        if (ReferenceEquals(sender, LocalGroupFilter))
        {
            var groups = localCatalog.Libraries.Select(item => item.Group).Distinct().Order().ToArray();
            int index = LocalGroupFilter.SelectedIndex - 1;
            selectedLocalGroup = index >= 0 && index < groups.Length ? groups[index] : null;
            selectedLocalLibrary = null; RenderLocalFilters();
        }
        else selectedLocalLibrary = (LocalLibraryFilter.SelectedItem as LibraryFilter)?.Id;
        await LoadPageAsync(1);
    }

    private async Task LoadLocalPageAsync(int requestedPage, bool rescan = false)
    {
        libraryWork.Cancel(); libraryWork.Dispose(); libraryWork = new();
        var ct = libraryWork.Token;
        var previousItems = TrackList.ItemsSource;
        var previousSelection = TrackList.SelectedItem;
        int previousPage = page, previousTotal = total;
        bool previousBack = PreviousPage.IsEnabled, previousNext = NextPage.IsEnabled;
        // Entering local music may still have a remote page on screen. Do not restore it as local music.
        if (previousItems is not IEnumerable<MusicTrack> previousTracks ||
            previousTracks.Any(track => !localSources.ContainsKey(track.SourceInstanceId)))
        {
            previousItems = null; previousSelection = null;
            previousPage = 1; previousTotal = 0; previousBack = previousNext = false;
        }
        TrackList.ItemsSource = null; PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        PageStatus.Text = "正在读取本地音乐…";
        try
        {
            if (!localCatalogLoaded) ApplyLocalCatalog(await localLibraryStore.LoadAsync(ct));
            if (rescan) ApplyLocalCatalog(await localLibraryStore.LoadAsync(ct));
            var definitions = FilteredLocalLibraries().ToArray();
            var scannedSources = new List<LocalMusicSource>();
            var failedSources = new List<Guid>();
            foreach (var definition in definitions)
            {
                if (!rescan && localSources.ContainsKey(definition.SourceId)) continue;
                try
                {
                    var source = await ScanLocalAsync(definition.Settings, ct
#if DEBUG
                        , localPageMetadataReader
#endif
                    );
                    if (ct.IsCancellationRequested || closed) return;
                    scannedSources.Add(source);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failedSources.Add(definition.SourceId); }
            }
            if (ct.IsCancellationRequested || closed) return;
            // Commit only after every scan finishes. Cancellation must not replace earlier libraries.
            foreach (var source in scannedSources) ReplaceLocalSource(source);
            foreach (var id in failedSources) unavailableLibraries.Add(id);
            RenderLocalFilters();
            var result = definitions.Where(item => localSources.ContainsKey(item.SourceId))
                .SelectMany(item => localSources[item.SourceId].Tracks.Select(track => track with { SourceLabel = item.Name })).Where(track => track.Title.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)
                    || track.Artist.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)).OrderBy(track => track.Title, StringComparer.OrdinalIgnoreCase).ToArray();
            total = result.Length; page = Math.Clamp(requestedPage, 1, Math.Max(1, (total + 49) / 50));
            TrackList.ItemsSource = result.Skip((page - 1) * 50).Take(50).ToArray();
            TrackList.SelectedIndex = total > 0 ? 0 : -1;
            int unavailable = definitions.Count(item => unavailableLibraries.Contains(item.SourceId));
            PageStatus.Text = definitions.Length == 0 ? "尚未导入本地音乐，请选择“导入文件夹”。" :
                $"本地音乐 · {definitions.Length} 个库 · 第 {page} 页，共 {total} 首" + (unavailable > 0 ? $" · {unavailable} 个库读取失败，配置与旧列表保留" : "");
            PreviousPage.IsEnabled = page > 1; NextPage.IsEnabled = page * 50 < total;
        }
        catch (OperationCanceledException)
        {
            if (!closed && !ct.IsCancellationRequested)
            {
                TrackList.ItemsSource = previousItems;
                TrackList.SelectedItem = previousSelection;
                page = previousPage; total = previousTotal;
                PreviousPage.IsEnabled = previousBack; NextPage.IsEnabled = previousNext;
                PageStatus.Text = previousItems is null ? "扫描已取消，请重试。" : "扫描已取消，显示扫描前的列表。";
            }
        }
        catch (Exception) { if (!closed && !ct.IsCancellationRequested) PageStatus.Text = "本地音乐库配置无法读取，原文件保留，请检查后重试。"; }
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
            var result = await LocalMusicSource.ScanAsync(settings, scan.Token, reader ?? Services.LocalMusicMetadataReader.ReadAsync, progress,
                reader is null ? localSources.GetValueOrDefault(settings.SourceId) : null);
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
