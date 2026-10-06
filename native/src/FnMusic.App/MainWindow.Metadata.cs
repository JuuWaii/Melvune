using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private readonly NasMetadataCache metadataCache = new(ThemeSettingsDirectory());
    private NasMetadataSnapshot? metadataSnapshot;
    private CancellationTokenSource? metadataWork;
    private Task? metadataTask;
    private NasApiClient? metadataClient;
    private bool metadataRefreshPending;
    private int metadataRequestedRevision;
    private int allMusicLoads;
    private global::Microsoft.UI.Dispatching.DispatcherQueueTimer? metadataNoticeTimer;
    private void ShowMetadataNotice(string text)
    {
        MetadataNotice.Text = text; MetadataNotice.Visibility = Visibility.Visible;
        if (metadataNoticeTimer is null)
        {
            metadataNoticeTimer = DispatcherQueue.CreateTimer(); metadataNoticeTimer.Interval = TimeSpan.FromSeconds(8);
            metadataNoticeTimer.IsRepeating = false;
            metadataNoticeTimer.Tick += (_, _) => { if (!closed) MetadataNotice.Visibility = Visibility.Collapsed; };
        }
        metadataNoticeTimer.Stop(); metadataNoticeTimer.Start();
    }
#if DEBUG
    private NasApiClient? metadataTestClient;
#endif
    private NasApiClient? MetadataClient =>
#if DEBUG
        metadataTestClient ??
#endif
        ViewModel.AuthenticatedClient;
    private NasMetadataSnapshot? CurrentMetadata(NasApiClient? api) => api is not null
        && ReferenceEquals(api, MetadataClient) && metadataSnapshot?.SourceId == api.SourceInstanceId ? metadataSnapshot : null;

    private void ResetMetadataSync()
    {
        metadataWork?.Cancel(); metadataWork = null;
        metadataClient = null; metadataTask = null; metadataSnapshot = null; metadataRefreshPending = false;
        UpdateBackgroundActivity();
        MetadataNotice.Visibility = Visibility.Collapsed;
        metadataNoticeTimer?.Stop();
    }
    private void RequestMetadataSync(bool refresh = false)
    {
        if (closed || MetadataClient is not { } api) return;
        if (refresh) metadataRequestedRevision++;
        if (ReferenceEquals(metadataClient, api) && metadataTask is { IsCompleted: false })
        { metadataRefreshPending |= refresh; return; }
        metadataWork?.Cancel();
        var work = CancellationTokenSource.CreateLinkedTokenSource(localLifetime.Token);
        metadataWork = work; metadataClient = api;
        MetadataNotice.Visibility = Visibility.Collapsed;
        UpdateBackgroundActivity();
        metadataTask = SyncMetadataAsync(api, work);
    }
    private async Task SyncMetadataAsync(NasApiClient api, CancellationTokenSource work)
    {
        var ct = work.Token;
        bool Current() => !closed && !ct.IsCancellationRequested && ReferenceEquals(api, MetadataClient)
            && ReferenceEquals(metadataWork, work);
        var progress = new Progress<MetadataSyncProgress>(value =>
        {
            if (Current())
            {
                metadataActivitySummary = "同步飞牛" + value.Stage + (value.Total > 0 ? $" · {value.Completed}/{value.Total}" : "…");
                UpdateBackgroundActivity();
            }
        });
        try
        {
            // A restored account must still be allowed to browse before its persisted catalog becomes visible.
            var permission = await api.ListTracksAsync(1, 1, ct);
            if (metadataSnapshot is null && permission.Total > 0)
            {
                var cached = await Task.Run(() => metadataCache.LoadAsync(api.SourceInstanceId, ct), ct);
                if (!Current()) return;
                metadataSnapshot = cached;
            }
            else if (permission.Total == 0) metadataSnapshot = null;
            do
            {
                metadataRefreshPending = false;
                int revision = metadataRequestedRevision;
                var result = await Task.Run(() => NasMetadataSync.RunAsync(api, metadataCache, ct, progress, snapshot =>
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!Current() || revision != metadataRequestedRevision) return;
                        metadataSnapshot = snapshot;
                        ApplyMetadataToVisibleCatalog(api);
                        if (RemoteSupports(MusicSourceCapabilities.ReadPlaylists)) RenderNasPlaylistNavigation(api,
                            snapshot.Playlists.Select(item => item.Playlist).ToArray(), (NasPlaylistNavigation.SelectedItem as NasPlaylist)?.Reference, true);
                    })), ct);
                if (!Current()) return;
                if (revision != metadataRequestedRevision) continue;
                metadataSnapshot = result.Snapshot;
                coverCache.Clear();
                ShowMetadataNotice(result.FailedAssets == 0 ? "飞牛资料同步完成" : $"资料已同步 · {result.FailedAssets} 项封面或歌词待重试");
            } while (metadataRefreshPending && Current());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (MusicApiException error) when (error.Failure == MusicFailure.Unauthorized)
        { if (Current()) await ViewModel.ExpireSessionAsync(); }
        catch (Exception)
        {
            if (Current()) ShowMetadataNotice("飞牛同步失败，原缓存保留；点击刷新重试");
        }
        finally
        {
            if (ReferenceEquals(metadataWork, work))
            { metadataWork = null; UpdateBackgroundActivity(); }
            work.Dispose();
        }
    }
    private static bool MetadataMatches(MusicTrack track, string query) => query.Length == 0
        || track.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || track.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
        || track.Album.Contains(query, StringComparison.OrdinalIgnoreCase);
    private void ApplyMetadataToVisibleCatalog(NasApiClient api)
    {
        if (allMusicLoads != 0 || !allMusicView || browseScope == BrowseScope.Local || LibraryPanel.Visibility != Visibility.Visible
            || LyricsPanel.Visibility == Visibility.Visible || activePlaylistPage is not null || fullPageView is not null
            || CurrentMetadata(api) is not { } snapshot || TrackList.ItemsSource is not IEnumerable<MusicTrack> current) return;
        var selected = (TrackList.SelectedItem as MusicTrack)?.Reference;
        var scroll = FindPresentationScroll(TrackList); double offset = scroll?.VerticalOffset ?? 0;
        var remote = snapshot.Tracks.Where(track => MetadataMatches(track, searchQuery)).Select(track => track with { SourceLabel = "飞牛音乐" }).ToArray();
        var local = browseScope == BrowseScope.Nas ? [] : current.Where(track => track.SourceInstanceId != api.SourceInstanceId).ToArray();
        var rows = remote.Concat(local).ToArray();
        TrackList.ItemsSource = rows; TrackList.SelectedItem = rows.FirstOrDefault(track => track.Reference == selected);
        total = rows.Length; page = 1;
        PageStatus.Text = $"{(browseScope == BrowseScope.Nas ? "飞牛音乐" : "所有音乐")} · 共 {total} 首"
            + (browseScope == BrowseScope.All ? $"（飞牛 {remote.Length} / 本地 {local.Length}）" : "");
        PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        TrackList.UpdateLayout(); scroll?.ChangeView(null, Math.Min(offset, scroll.ScrollableHeight), null, true);
    }
    private TrackPage CachedTrackPage(NasMetadataSnapshot snapshot, int requestedPage, bool favorites = false, string query = "")
    {
        var tracks = (favorites ? snapshot.Favorites : snapshot.Tracks).Where(track => MetadataMatches(track, query)).ToArray();
        return new(tracks.Skip((Math.Max(1, requestedPage) - 1) * 50).Take(50).ToArray(), tracks.Length);
    }
    private async Task<byte[]?> ReadRemoteCoverAsync(NasApiClient api, string id, NasCoverSize size, CancellationToken ct)
    {
        var bytes = await metadataCache.ReadCoverAsync(api.SourceInstanceId, id, NasCoverSize.Large, ct)
            ?? (size == NasCoverSize.Large ? null : await metadataCache.ReadCoverAsync(api.SourceInstanceId, id, size, ct));
        if (bytes is not null) return bytes;
        bytes = await api.GetCoverAsync(id, size, ct);
        if (bytes is { Length: > 0 })
            try { await metadataCache.SaveCoverAsync(api.SourceInstanceId, id, size, bytes, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Playback can use the downloaded image without a disk cache. */ }
        return bytes;
    }
    private async Task<Lyrics> ReadRemoteLyricsAsync(NasApiClient api, TrackReference track, CancellationToken ct)
    {
        var lyrics = await metadataCache.ReadLyricsAsync(track, ct);
        if (lyrics is not null) return lyrics;
        lyrics = await api.GetLyricsAsync(track, ct);
        try { await metadataCache.SaveLyricsAsync(track, lyrics, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return lyrics;
    }
}
