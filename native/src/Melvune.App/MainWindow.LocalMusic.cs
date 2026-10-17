using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melvune.App;

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
        allMusicView = false; localView = true; favoritesView = false; parentArtist = null; collectionKind = null; collection = null;
        SearchInput.Text = searchQuery = ""; UpdateCollectionView();
        await LoadPageAsync(1);
    }

    private async void ImportLocal_Click(object sender, RoutedEventArgs e) => await ShowLocalLibraryManagementAsync(createNew: true);

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
            if (queue.Current?.SourceInstanceId == id) StopUnavailableSource(id);
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
            try
            {
                var cached = await ReadLocalIndexAsync(definition.Settings, ct);
                var source = cached ?? await ScanLocalAsync(definition.Settings, ct);
                ReplaceLocalSource(source);
                if (cached is not null) RequestLocalIndexRefresh(definition.SourceId);
                else await SaveLocalIndexAsync(source);
            }
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
        if (updatingLibraryFilters || !(localView || allMusicView)) return;
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
            var cachedSources = new HashSet<Guid>();
            var failedSources = new List<Guid>();
            foreach (var definition in definitions)
            {
                if (!rescan && localSources.ContainsKey(definition.SourceId)) continue;
                try
                {
                    var cached = !rescan ? await ReadLocalIndexAsync(definition.Settings, ct) : null;
                    var source = cached ?? await ScanLocalAsync(definition.Settings, ct
#if DEBUG
                        , localPageMetadataReader
#endif
                    );
                    if (ct.IsCancellationRequested || closed) return;
                    scannedSources.Add(source);
                    if (cached is not null) cachedSources.Add(source.Source.Id);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failedSources.Add(definition.SourceId); }
            }
            if (ct.IsCancellationRequested || closed) return;
            // Commit only after every scan finishes. Cancellation must not replace earlier libraries.
            foreach (var source in scannedSources) ReplaceLocalSource(source);
            foreach (var source in scannedSources)
            { if (cachedSources.Contains(source.Source.Id)) RequestLocalIndexRefresh(source.Source.Id); else await SaveLocalIndexAsync(source); }
            if (ct.IsCancellationRequested || closed) return;
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
        finally { if (!closed && !ct.IsCancellationRequested && localIndexVisibleRefreshPending) RefreshVisibleLocalIndex(); }
    }

    private void CancelLocalScan_Click(object sender, RoutedEventArgs e)
    { if (localLibraryWork is { IsCancellationRequested: false } background) background.Cancel(); else if (localScan is not null) localScan.Cancel(); else localIndexWork?.Cancel(); }

    private async Task<LocalMusicSource> ScanLocalAsync(LocalMusicSettings settings, CancellationToken parent,
        Func<string, CancellationToken, Task<LocalMusicMetadata>>? reader = null, bool background = false)
    {
        if (!background) localScan?.Cancel();
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(parent, localLifetime.Token);
        if (background) backgroundLocalScan = scan; else localScan = scan;
        bool Current() => ReferenceEquals(background ? backgroundLocalScan : localScan, scan);
        CancelLocalScan.Visibility = Visibility.Visible;
        ReportLocalLoading("正在扫描本地音乐…", background); UpdateBackgroundActivity();
        var progress = new Progress<int>(count =>
        {
            if (!closed && Current() && !scan.IsCancellationRequested)
            {
                ReportLocalLoading($"已读取 {count} 首，正在扫描…", background);
            }
        });
        try
        {
            var result = await LocalMusicSource.ScanAsync(settings, scan.Token, reader ?? Services.LocalMusicMetadataReader.ReadAsync, progress,
                reader is null ? localSources.GetValueOrDefault(settings.SourceId) : null);
            scan.Token.ThrowIfCancellationRequested();
            if (!closed && Current()) ReportLocalLoading($"扫描完成，共 {result.Tracks.Count} 首。", background);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!closed && Current()) ReportLocalLoading("扫描已取消，原音乐库保留。", background);
            throw;
        }
        catch
        {
            if (!closed && Current()) ReportLocalLoading("扫描失败，原音乐库保留。", background);
            throw;
        }
        finally
        {
            if (Current())
            {
                if (background) backgroundLocalScan = null; else localScan = null;
                if (!closed && localScan is null && backgroundLocalScan is null && localLibraryWork is null) CancelLocalScan.Visibility = Visibility.Collapsed;
                UpdateBackgroundActivity();
            }
        }
    }
}
