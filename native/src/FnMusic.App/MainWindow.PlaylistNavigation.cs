using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? playlistNavigationWork;
    private long localNavigationRevision = -1;
    private int nasNavigationVersion;

    private void RenderLocalPlaylistNavigation(LocalPlaylistCatalog catalog, Guid? selected = null, bool background = false)
    {
        if (closed) return;
        if (background && catalog.Revision < localNavigationRevision) return;
        localNavigationRevision = catalog.Revision;
        bool changed = !(LocalPlaylistNavigation.ItemsSource as IEnumerable<LocalPlaylist> ?? []).Select(item => (item.Id, item.Name))
            .SequenceEqual(catalog.Playlists.Select(item => (item.Id, item.Name)));
        LocalPlaylistNavigation.ItemsSource = catalog.Playlists;
        LocalPlaylistNavigation.SelectedItem = catalog.Playlists.FirstOrDefault(item => item.Id == selected);
        LocalPlaylistNavigationStatus.Text = catalog.Playlists.Count == 0 ? "暂无本地歌单，点击 + 新建。" : "";
        if (changed && SidebarHost.Visibility == Visibility.Visible) AnimateViewEntry(LocalPlaylistNavigation);
    }
    private void RenderNasPlaylistNavigation(NasApiClient api, IReadOnlyList<NasPlaylist> playlists, PlaylistReference? selected = null, bool background = false)
    {
        if (closed || !IsCurrentPlaylistClient(api)) return;
        if (!background) ++nasNavigationVersion;
        bool changed = !(NasPlaylistNavigation.ItemsSource as IEnumerable<NasPlaylist> ?? []).Select(item => (item.Reference, item.Name))
            .SequenceEqual(playlists.Select(item => (item.Reference, item.Name)));
        NasPlaylistNavigation.ItemsSource = playlists;
        NasPlaylistNavigation.SelectedItem = playlists.FirstOrDefault(item => item.Reference == selected);
        NasPlaylistNavigationStatus.Text = playlists.Count == 0 ? "暂无飞牛歌单。" : "";
        if (changed && SidebarHost.Visibility == Visibility.Visible) AnimateViewEntry(NasPlaylistNavigation);
    }
    private async Task RefreshPlaylistNavigationAsync()
    {
        playlistNavigationWork?.Cancel(); playlistNavigationWork?.Dispose();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(localLifetime.Token);
        playlistNavigationWork = work;
        work.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = work.Token;
        var api = ViewModel.AuthenticatedClient;
        int nasVersion = nasNavigationVersion;
        try
        {
            try
            {
                var catalog = await playlistStore.LoadAsync(ct);
                ct.ThrowIfCancellationRequested();
                RenderLocalPlaylistNavigation(catalog, (LocalPlaylistNavigation.SelectedItem as LocalPlaylist)?.Id, background: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { if (!closed) LocalPlaylistNavigationStatus.Text = "歌单读取失败，请打开本地歌单刷新。"; }
            if (api is null || !ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists))
            { if (!closed) ClearNasPlaylistNavigation(); return; }
            try
            {
                var playlists = CurrentMetadata(api)?.Playlists.Select(item => item.Playlist).ToArray() ?? await api.ListPlaylistsAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (nasVersion == nasNavigationVersion) RenderNasPlaylistNavigation(api, playlists, (NasPlaylistNavigation.SelectedItem as NasPlaylist)?.Reference, background: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception)
            {
                if (!closed && IsCurrentPlaylistClient(api)) NasPlaylistNavigationStatus.Text = "读取失败，请打开飞牛歌单刷新。";
            }
        }
        finally { if (ReferenceEquals(playlistNavigationWork, work)) playlistNavigationWork = null; }
    }
    private void ClearNasPlaylistNavigation()
    {
        ++nasNavigationVersion;
        NasPlaylistNavigation.ItemsSource = null;
        NasPlaylistNavigationStatus.Text = "连接飞牛并登录后显示歌单。";
    }
    private void ResetPlaylistNavigation()
    {
        if (fullPageView?.RemoteSource is not null) CloseFullPage(force: true, restoreFocus: false);
        CloseTrackContextMenu();
        ++contentNavigationVersion;
        playlistNavigationWork?.Cancel();
        ClearNasPlaylistNavigation();
        if (activePlaylistPage?.RemoteSource is not null)
        {
            ClosePlaylistPage();
            if (LyricsPanel.Visibility == Visibility.Visible)
            {
                lyricsPlaylistVisibility = lyricsSettingsVisibility = lyricsLibraryVisibility = Visibility.Collapsed;
                lyricsHomeVisibility = Visibility.Visible;
            }
            else HomePanel.Visibility = Visibility.Visible;
        }
    }
    private async void LocalPlaylistNavigation_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not LocalPlaylist playlist || closed) return;
        try { await ShowLocalPlaylistsAsync(playlist.Id); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "本地歌单无法读取，请刷新后重试。"; }
    }
    private async void NewLocalPlaylist_Click(object sender, RoutedEventArgs args)
    {
        if (closed) return;
        try { await ShowLocalPlaylistsAsync(createNew: true); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "本地歌单无法读取，请刷新后重试。"; }
    }
    private async void NasPlaylistNavigation_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not NasPlaylist playlist || ViewModel.AuthenticatedClient is not { } api
            || playlist.Reference.SourceInstanceId != api.SourceInstanceId || !RequireRemoteCapability(MusicSourceCapabilities.ReadPlaylists)) return;
        try { await ShowNasPlaylistsAsync(api, playlist.Reference); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "飞牛歌单无法读取，请刷新后重试。"; }
    }
    private async void NewNasPlaylist_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.AuthenticatedClient is not { } api
            || !RequireRemoteCapability(MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists)) return;
        try { await ShowNasPlaylistsAsync(api, createNew: true); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "飞牛歌单无法读取，请刷新后重试。"; }
    }
}
