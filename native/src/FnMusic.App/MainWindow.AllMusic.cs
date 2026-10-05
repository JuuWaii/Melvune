using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private enum BrowseScope { All, Nas, Local }
    private bool allMusicView, updatingBrowseSource;
    private BrowseScope browseScope;
#if DEBUG
    private NasApiClient? allMusicTestClient;
#endif
    private NasApiClient? AllMusicClient =>
#if DEBUG
        allMusicTestClient ??
#endif
        ViewModel.AuthenticatedClient;
    private async void AllMusic_Click(object sender, RoutedEventArgs e) => await ShowAllMusicAsync();

    private async Task ShowAllMusicAsync(string initialQuery = "")
    {
        if (!await NavigateMainContentAsync(LibraryPanel)) return;
        allMusicView = true; localView = false; browseScope = BrowseScope.All;
        favoritesView = false; parentArtist = null; collectionKind = null; collection = null;
        SearchInput.Text = searchQuery = initialQuery; selectedLocalGroup = null; selectedLocalLibrary = null;
        RenderLocalFilters(); UpdateCollectionView();
        await LoadPageAsync(1);
    }

    private void UpdateBrowseSourceFilter()
    {
        updatingBrowseSource = true;
        BrowseSourceFilter.Visibility = allMusicView ? Visibility.Visible : Visibility.Collapsed;
        BrowseSourceFilter.SelectedIndex = (int)browseScope;
        Grid.SetColumn(LocalLibraryFilters, allMusicView ? 1 : 0);
        Grid.SetColumnSpan(LocalLibraryFilters, allMusicView ? 1 : 2);
        Grid.SetColumnSpan(BrowseSourceFilter, browseScope == BrowseScope.Nas ? 2 : 1);
        updatingBrowseSource = false;
    }

    private async void BrowseSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updatingBrowseSource || !allMusicView || BrowseSourceFilter.SelectedIndex < 0) return;
        browseScope = (BrowseScope)BrowseSourceFilter.SelectedIndex;
        UpdateCollectionView(); AnimateViewEntry(LibraryPanel);
        await LoadPageAsync(1);
    }

    private async Task LoadAllMusicPageAsync(int requestedPage, bool rescan = false)
    {
        libraryWork.Cancel(); libraryWork.Dispose(); libraryWork = new();
        var ct = libraryWork.Token;
        var scope = browseScope; string query = searchQuery;
        var api = AllMusicClient;
        var oldItems = TrackList.ItemsSource; var oldSelection = TrackList.SelectedItem;
        int oldPage = page, oldTotal = total;
        bool oldBack = PreviousPage.IsEnabled, oldNext = NextPage.IsEnabled;
        PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        PageStatus.Text = rescan ? "正在扫描并汇总音乐…" : "正在读取所有音乐…";
        try
        {
            var catalog = await localLibraryStore.LoadAsync(ct);
            var definitions = catalog.Libraries.Where(item => (selectedLocalGroup is null || item.Group == selectedLocalGroup)
                && (selectedLocalLibrary is null || item.SourceId == selectedLocalLibrary)).ToArray();
            var staged = new Dictionary<Guid, LocalMusicSource>();
            var failed = new List<Guid>();
            if (scope != BrowseScope.Nas)
                foreach (var definition in definitions)
                {
                    if (!rescan && localSources.TryGetValue(definition.SourceId, out var source)
                        && string.Equals(LocalLibraryStore.NormalizePath(source.Settings.Directory), LocalLibraryStore.NormalizePath(definition.Directory), StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        staged[definition.SourceId] = await ScanLocalAsync(definition.Settings, ct
#if DEBUG
                            , localPageMetadataReader
#endif
                        );
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { failed.Add(definition.SourceId); }
                }
            var localTracks = scope == BrowseScope.Nas ? [] : definitions.SelectMany(item =>
            {
                var source = staged.GetValueOrDefault(item.SourceId) ?? localSources.GetValueOrDefault(item.SourceId);
                // A cache from an old folder must not appear under the new folder's identity.
                return source is not null && string.Equals(LocalLibraryStore.NormalizePath(source.Settings.Directory), LocalLibraryStore.NormalizePath(item.Directory), StringComparison.OrdinalIgnoreCase)
                    ? source.Tracks.Select(track => track with { SourceLabel = item.Name }) : [];
            }).Where(track => track.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || track.Artist.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(track => track.Title, StringComparer.OrdinalIgnoreCase).ThenBy(track => track.SourceInstanceId).ThenBy(track => track.Id).ToArray();

            var capability = query.Length == 0 ? MusicSourceCapabilities.Browse : MusicSourceCapabilities.Search;
            string remoteStatus = "";
            int remoteTotal = 0;
            IReadOnlyList<MusicTrack> remoteTracks = [];
            async Task<TrackPage> ReadRemote(int index)
            {
#if DEBUG
                if (IsSyntheticPreview && api is null) return await GetSyntheticPageAsync(query, index);
#endif
                return query.Length == 0 ? await api!.ListTracksAsync(index, 50, ct) : await api!.SearchTracksAsync(query, index, 50, ct);
            }
            if (scope != BrowseScope.Local)
            {
                if (!RemoteSupports(capability)) remoteStatus = ViewModel.AuthenticatedClient is null ? "飞牛未连接" : "飞牛不支持当前浏览或搜索";
                else
                {
                    try
                    {
                        bool reading = true;
                        var progress = new Progress<int>(count =>
                        {
                            if (reading && !closed && !ct.IsCancellationRequested && allMusicView && ReferenceEquals(api, AllMusicClient))
                                PageStatus.Text = $"正在读取飞牛歌曲信息… 已读取 {count} 首";
                        });
                        try
                        {
                            remoteTracks = (await TrackCatalogReader.ReadAsync((index, _) => ReadRemote(index), ct, progress))
                                .Select(track => track with { SourceLabel = "飞牛音乐" }).ToArray();
                        }
                        finally { reading = false; }
                        remoteTotal = remoteTracks.Count;
                    }
                    catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
                    {
                        if (!ct.IsCancellationRequested && ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync();
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { remoteTotal = 0; remoteTracks = []; remoteStatus = "飞牛读取失败，请刷新重试"; }
                }
            }
            ct.ThrowIfCancellationRequested();
            if (closed || !allMusicView || !ReferenceEquals(api, AllMusicClient)) return;
            if ((await localLibraryStore.LoadAsync(ct)).Revision != catalog.Revision) throw new LocalLibraryConflictException();
            ct.ThrowIfCancellationRequested();
            if (closed || !allMusicView || !ReferenceEquals(api, AllMusicClient)) return;
            if (!localCatalogLoaded || localCatalog.Revision != catalog.Revision || !localCatalog.Libraries.SequenceEqual(catalog.Libraries)) ApplyLocalCatalog(catalog);
            foreach (var source in staged.Values) ReplaceLocalSource(source);
            foreach (var id in failed) unavailableLibraries.Add(id);
            RenderLocalFilters();
            total = checked(remoteTotal + localTracks.Length);
            page = 1;
            var rows = remoteTracks.Concat(localTracks).ToArray();
            TrackList.ItemsSource = rows;
            TrackList.SelectedIndex = oldSelection is MusicTrack selected ? Array.FindIndex(rows, track => track.Reference == selected.Reference) : -1;
            if (TrackList.SelectedIndex < 0 && rows.Length > 0) TrackList.SelectedIndex = 0;
            string scopeName = scope == BrowseScope.All ? "所有音乐" : scope == BrowseScope.Nas ? "飞牛音乐" : "本地音乐";
            PageStatus.Text = $"{scopeName} · 共 {total} 首" + (scope == BrowseScope.All ? $"（飞牛 {remoteTotal} / 本地 {localTracks.Length}）" : "")
                + (remoteStatus.Length > 0 ? " · " + remoteStatus : "") + (failed.Count > 0 ? $" · {failed.Count} 个本地库读取失败，配置与可用缓存保留" : "");
            PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        }
        catch (OperationCanceledException)
        {
            if (!closed && !ct.IsCancellationRequested)
            {
                TrackList.ItemsSource = oldItems; TrackList.SelectedItem = oldSelection; page = oldPage; total = oldTotal;
                PreviousPage.IsEnabled = oldBack; NextPage.IsEnabled = oldNext; PageStatus.Text = "扫描已取消，显示扫描前的列表。";
            }
        }
        catch (Exception)
        { if (!closed && !ct.IsCancellationRequested) { PageStatus.Text = "本地音乐库配置读取失败或已变更，请刷新重试；原列表保留。"; PreviousPage.IsEnabled = oldBack; NextPage.IsEnabled = oldNext; } }
    }
}
