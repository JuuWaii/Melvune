using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private readonly LocalMusicIndexStore localIndexStore = new(ThemeSettingsDirectory());
    private readonly HashSet<Guid> localIndexPending = [];
    private Task? localIndexTask;
    private CancellationTokenSource? localIndexWork;
    private bool localIndexVisibleRefreshPending;
    private bool LocalIndexEnabled => !IsSyntheticPreview
#if DEBUG
        || Environment.GetCommandLineArgs().Contains("--verify-local-cache-window")
#endif
        ;

    private Task<LocalMusicSource?> ReadLocalIndexAsync(LocalMusicSettings settings, CancellationToken ct) =>
        !LocalIndexEnabled ? Task.FromResult<LocalMusicSource?>(null)
        : Task.Run(() => localIndexStore.LoadAsync(settings, ct), ct);

    private async Task SaveLocalIndexAsync(LocalMusicSource source)
    {
        if (!LocalIndexEnabled || closed) return;
        try { await Task.Run(() => localIndexStore.SaveAsync(source, localLifetime.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) ShowMetadataNotice("本地歌曲索引未能保存，本次仍可播放；下次启动将重新扫描。"); }
    }

    private void RequestLocalIndexRefresh(Guid id)
    {
        if (!LocalIndexEnabled || closed) return;
        localIndexPending.Add(id);
        if (localIndexTask is { IsCompleted: false }) return;
        localIndexTask = RefreshLocalIndexesAsync();
    }

    private async Task RestoreLocalIndexesAtStartupAsync()
    {
        try
        {
            if (!localCatalogLoaded) ApplyLocalCatalog(await localLibraryStore.LoadAsync(localLifetime.Token));
            foreach (var definition in localCatalog.Libraries.ToArray())
            {
                var cached = await ReadLocalIndexAsync(definition.Settings, localLifetime.Token);
                if (closed) return;
                if (cached is not null) ReplaceLocalSource(cached);
                RequestLocalIndexRefresh(definition.SourceId);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) ShowMetadataNotice("本地音乐库索引无法读取，请进入音乐库管理检查。"); }
    }

    private async Task RefreshLocalIndexesAsync()
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(localLifetime.Token);
        var ct = work.Token;
        try
        {
            // Let cached rows render before starting disk enumeration; navigation does not cancel this work.
            await Task.Delay(300, ct);
            while (localIndexPending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (localImportBusy || localScan is not null || allMusicLoads > 0)
                { await Task.Delay(100, ct); continue; }
                Guid id = localIndexPending.First(); localIndexPending.Remove(id);
                var definition = localCatalog.Libraries.FirstOrDefault(item => item.SourceId == id);
                if (definition is null) continue;
                var previous = localSources.GetValueOrDefault(id);
                long revision = localCatalog.Revision;
                localActivitySummary = "正在后台检查本地歌曲…";
                localIndexWork = work; CancelLocalScan.Visibility = Visibility.Visible; UpdateBackgroundActivity();
                var progress = new Progress<int>(count =>
                {
                    if (!closed && !ct.IsCancellationRequested && ReferenceEquals(localIndexWork, work))
                    { localActivitySummary = $"后台检查本地歌曲 · 已读取 {count} 首"; UpdateBackgroundActivity(); }
                });
                try
                {
                    var refreshed = await LocalMusicSource.ScanAsync(definition.Settings, ct,
#if DEBUG
                        localPageMetadataReader ??
#endif
                        Services.LocalMusicMetadataReader.ReadAsync, progress, previous);
                    ct.ThrowIfCancellationRequested();
                    // Editing/relinking/manual scans win over an older background result.
                    if (closed || localImportBusy || localCatalog.Revision != revision
                        || !ReferenceEquals(previous, localSources.GetValueOrDefault(id))
                        || (await localLibraryStore.LoadAsync(ct)).Revision != revision) continue;
                    if (previous is not null)
                    { previous.ApplyIndexRefresh(refreshed); unavailableLibraries.Remove(id); coverCache.Clear(); RenderLocalFilters(); }
                    else ReplaceLocalSource(refreshed);
                    await SaveLocalIndexAsync(refreshed);
                    localIndexVisibleRefreshPending = true;
                    RefreshVisibleLocalIndex();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    if (!closed && localCatalog.Revision == revision)
                    { unavailableLibraries.Add(id); RenderLocalFilters(); ShowMetadataNotice("本地音乐库后台检查失败，已保留缓存；请检查磁盘或网络连接后刷新。"); }
                }
                finally
                {
                    localIndexWork = null;
                    if (!closed && localScan is null && backgroundLocalScan is null && localLibraryWork is null) CancelLocalScan.Visibility = Visibility.Collapsed;
                    UpdateBackgroundActivity();
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { localIndexWork = null; UpdateBackgroundActivity(); }
    }

    private void RefreshVisibleLocalIndex()
    {
        if (closed || allMusicLoads != 0 || !(localView || allMusicView) || LibraryPanel.Visibility != Visibility.Visible
            || LyricsPanel.Visibility == Visibility.Visible || fullPageView is not null || activePlaylistPage is not null
            || TrackList.ItemsSource is not IEnumerable<MusicTrack> current) return;
        localIndexVisibleRefreshPending = false;
        var selected = (TrackList.SelectedItem as MusicTrack)?.Reference;
        var scroll = FindPresentationScroll(TrackList); double offset = scroll?.VerticalOffset ?? 0;
        var definitions = FilteredLocalLibraries().ToArray();
        var local = allMusicView && browseScope is BrowseScope.Nas or BrowseScope.Navidrome ? [] : definitions.SelectMany(item =>
            localSources.TryGetValue(item.SourceId, out var source) ? source.Tracks.Select(track => track with { SourceLabel = item.Name }) : [])
            .Where(track => track.Title.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) || track.Artist.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(track => track.Title, StringComparer.OrdinalIgnoreCase).ThenBy(track => track.SourceInstanceId).ThenBy(track => track.Id).ToArray();
        MusicTrack[] rows;
        if (allMusicView)
        {
            var remote = current.Where(track => !localCatalog.Libraries.Any(item => item.SourceId == track.SourceInstanceId)).ToArray();
            rows = remote.Concat(local).ToArray(); total = rows.Length; page = 1;
            string scope = browseScope == BrowseScope.All ? "所有音乐" : browseScope == BrowseScope.Local ? "本地音乐" : browseScope == BrowseScope.Navidrome ? "Navidrome" : "飞牛音乐";
            int navCount = remote.Count(track => navidromeSources.ContainsKey(track.SourceInstanceId));
            PageStatus.Text = $"{scope} · 共 {total} 首" + (browseScope == BrowseScope.All ? $"（飞牛 {remote.Length - navCount} / 本地 {local.Length} / Navidrome {navCount}）" : "");
            PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        }
        else
        {
            total = local.Length; page = Math.Clamp(page, 1, Math.Max(1, (total + 49) / 50));
            rows = local.Skip((page - 1) * 50).Take(50).ToArray();
            PageStatus.Text = $"本地音乐 · {definitions.Length} 个库 · 第 {page} 页，共 {total} 首";
            PreviousPage.IsEnabled = page > 1; NextPage.IsEnabled = page * 50 < total;
        }
        TrackList.ItemsSource = rows; TrackList.SelectedItem = rows.FirstOrDefault(track => track.Reference == selected);
        TrackList.UpdateLayout(); scroll?.ChangeView(null, Math.Min(offset, scroll.ScrollableHeight), null, true);
    }
}
