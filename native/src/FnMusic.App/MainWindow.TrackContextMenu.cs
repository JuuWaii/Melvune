using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private MenuFlyout? trackContextMenu;
    private bool contextPlaylistWriteBusy;
    private NasApiClient? ContextClient => ViewModel.AuthenticatedClient
#if DEBUG
        ?? (IsSyntheticPreview ? playlistTestClient : null)
#endif
        ;
    private void TrackActions_Click(object sender, RoutedEventArgs args)
    {
        if (TrackList.SelectedItem is MusicTrack track) ShowTrackContextMenu(TrackList, track, true, anchor: sender as FrameworkElement);
    }

    private static MusicTrack? ContextTrack(object? item) => item switch
    {
        MusicTrack track => track,
        PlaylistRow row => row.Track,
        _ => null
    };
    private static object? ResolveContextItem(ListView list, DependencyObject? origin, bool keyboard)
    {
        for (var current = origin; current is not null && !ReferenceEquals(current, list); current = VisualTreeHelper.GetParent(current))
            if (current is ListViewItem container && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(container), list))
                return container.Content;
        // A pointer on empty space must never fall back to an old selected track.
        return keyboard ? list.SelectedItem : null;
    }
    private void TrackList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not ListView list || closed || !list.IsEnabled) return;
        bool pointer = args.TryGetPosition(list, out var position);
        var origin = pointer ? args.OriginalSource as DependencyObject : FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        var item = ResolveContextItem(list, origin, !pointer);
        if (ContextTrack(item) is not { } track) return;
        list.SelectedItem = item;
        ShowTrackContextMenu(list, track, item is MusicTrack && !ReferenceEquals(list, queuePage?.List), pointer ? position : null);
        args.Handled = true;
    }
    private void TrackContext_Canceled(UIElement sender, RoutedEventArgs args) => CloseTrackContextMenu();
    private void CloseTrackContextMenu() { trackContextMenu?.Hide(); trackContextMenu = null; }
    private void ShowTrackContextMenu(ListView list, MusicTrack track, bool favoriteStateKnown, global::Windows.Foundation.Point? position = null, FrameworkElement? anchor = null)
    {
        CloseTrackContextMenu();
        var menu = CreateTrackContextMenu(list, track, favoriteStateKnown);
        ShowContextMenu(menu, list, position, anchor);
    }
    private void ShowContextMenu(MenuFlyout menu, ListView list, global::Windows.Foundation.Point? position = null, FrameworkElement? anchor = null)
    {
        CloseTrackContextMenu();
        trackContextMenu = menu;
        void ThemeChanged(FrameworkElement _, object args)
        { if (ReferenceEquals(trackContextMenu, menu)) CloseTrackContextMenu(); else menu.Hide(); }
        Root.ActualThemeChanged += ThemeChanged;
        menu.Closed += (_, _) =>
        {
            Root.ActualThemeChanged -= ThemeChanged;
            if (ReferenceEquals(trackContextMenu, menu)) trackContextMenu = null;
        };
        try { if (position is { } point) menu.ShowAt(list, point); else menu.ShowAt(anchor ?? list); }
        catch { Root.ActualThemeChanged -= ThemeChanged; if (ReferenceEquals(trackContextMenu, menu)) trackContextMenu = null; throw; }
    }
    private bool ContextPlayable(MusicTrack track) => !track.IsCue && TrackSupports(track, MusicSourceCapabilities.Playback)
        && (localSources.ContainsKey(track.SourceInstanceId) ? PlaylistTrackAvailable(LocalPlaylistEntry.FromTrack(track)) : true);
    private bool ContextRemoteBrowse(MusicTrack track) => ContextClient?.SourceInstanceId == track.SourceInstanceId
        && TrackSupports(track, MusicSourceCapabilities.Browse);

    private MenuFlyout CreateTrackContextMenu(ListView list, MusicTrack track, bool favoriteStateKnown)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Command(string text, object tag, bool enabled, Func<Task> run)
        {
            var command = new MenuFlyoutItem { Text = text, Tag = tag, IsEnabled = enabled };
            command.Click += async (_, _) => await RunContextCommandAsync(run);
            return command;
        }
        menu.Items.Add(Command("播放", "play", ContextPlayable(track), () => PlayContextTrackAsync(list, track)));
        menu.Items.Add(Command("下一首播放", "next", ContextPlayable(track) && queue.Current?.Reference != track.Reference, () =>
        {
            if (!ContextPlayable(track)) { PlaybackStatus.Text = "歌曲来源未连接或曲目不可用。"; return Task.CompletedTask; }
            if (TryInsertPlaybackNext(track))
            {
                if (queue.Count == 1) queueOrigin = "歌曲菜单";
                UpdateQueueStatus(); PlaybackStatus.Text = "已加入下一首播放，当前播放保持。";
            }
            return Task.CompletedTask;
        }));
        var favorite = Command(track.IsFavorite ? "取消收藏" : "收藏", "favorite",
            favoriteStateKnown && !favoriteBusy && CanFavoriteTrack(track), async () =>
            {
                await ToggleFavoriteTrackAsync(track);
                if (!closed && activePlaylistPage is { RemoteSource: not null, Reload: { } reload }) await reload();
            });
        if (!favoriteStateKnown) ToolTipService.SetToolTip(favorite, "歌单快照未提供收藏状态，请从飞牛歌曲列表操作。" );
        menu.Items.Add(favorite);
        menu.Items.Add(new MenuFlyoutSeparator());
        var local = new MenuFlyoutSubItem { Text = "加入本地歌单", Tag = "local-playlists", IsEnabled = !contextPlaylistWriteBusy && track.SourceInstanceId != Guid.Empty };
        foreach (var playlist in (LocalPlaylistNavigation.ItemsSource as IEnumerable<LocalPlaylist> ?? []).Take(500))
            local.Items.Add(Command(playlist.Name, playlist.Id.ToString(), true, () => AddContextToLocalPlaylistAsync(track, playlist.Id)));
        local.Items.Add(Command("新建本地歌单…", "new-local", true, () => ShowLocalPlaylistsAsync(createNew: true, picked: track)));
        menu.Items.Add(local);
        var api = ContextClient;
        var remote = new MenuFlyoutSubItem { Text = "加入飞牛歌单", Tag = "nas-playlists", IsEnabled = !contextPlaylistWriteBusy
            && api?.SourceInstanceId == track.SourceInstanceId
            && TrackSupports(track, MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists) };
        foreach (var playlist in (NasPlaylistNavigation.ItemsSource as IEnumerable<NasPlaylist> ?? []).Where(p => p.Reference.SourceInstanceId == track.SourceInstanceId))
            remote.Items.Add(Command(playlist.Name, playlist.Reference, true, () => AddContextToNasPlaylistAsync(track, playlist.Reference)));
        remote.Items.Add(Command("新建飞牛歌单…", "new-nas", api is not null, async () =>
        {
            if (api is not null && IsCurrentPlaylistClient(api) && api.SourceInstanceId == track.SourceInstanceId)
                await ShowNasPlaylistsAsync(api, createNew: true, pickedTrack: track);
        }));
        menu.Items.Add(remote);
        menu.Items.Add(new MenuFlyoutSeparator());
        var artists = new MenuFlyoutSubItem { Text = "查看歌手", Tag = "artists", IsEnabled = ContextRemoteBrowse(track) && track.Artists.Count > 0 };
        foreach (var artist in track.Artists)
            artists.Items.Add(Command(artist.Name, artist.Id, true, () => OpenContextCollectionAsync(track, CollectionKind.Artist, artist.Id, artist.Name)));
        menu.Items.Add(artists);
        menu.Items.Add(Command("查看专辑", "album", ContextRemoteBrowse(track) && !string.IsNullOrEmpty(track.AlbumId),
            () => OpenContextCollectionAsync(track, CollectionKind.Album, track.AlbumId!, track.Album)));
        ApplyMenuTheme(menu);
        return menu;
    }
    private async Task RunContextCommandAsync(Func<Task> run)
    {
        if (closed) return;
        try { await run(); }
        catch (OperationCanceledException) { if (!closed && !localLifetime.IsCancellationRequested) PlaybackStatus.Text = "操作已取消或超时，请刷新核对后重试。"; }
        catch (PlaylistConflictException) { if (!closed) PlaybackStatus.Text = "本地歌单已在其他窗口修改，请刷新核对后重试。"; }
        catch (RemotePlaylistConflictException) { if (!closed) PlaybackStatus.Text = "飞牛歌单已变化，请刷新核对后重试。"; }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "操作未能完成，请刷新核对后重试。"; }
    }
    private async Task PlayContextTrackAsync(ListView list, MusicTrack track)
    {
        if (!ContextPlayable(track) || !list.IsLoaded) return;
        if (ReferenceEquals(list, queuePage?.List))
        { if (queue.Select(track.Reference) is { } selected) await PlayTrackAsync(selected); return; }
        var tracks = list.Items.Cast<object>().Select(ContextTrack).OfType<MusicTrack>().Where(ContextPlayable).ToArray();
        if (!TryReplacePlaybackQueue(tracks, track.Reference)) return;
        queueOrigin = ReferenceEquals(list, TrackList) ? allMusicView ? "所有音乐当前列表" : localView ? "本地音乐当前页" : "飞牛歌曲当前页" : "歌单页面";
        await PlayTrackAsync(track);
    }
    private async Task OpenContextCollectionAsync(MusicTrack track, CollectionKind kind, string id, string name)
    {
        if (!ContextRemoteBrowse(track) || !await NavigateMainContentAsync(LibraryPanel)) return;
        if (!ContextRemoteBrowse(track)) return;
        allMusicView = localView = favoritesView = false; parentArtist = null;
        collectionKind = kind; collection = new MusicCollection(id, name, null); collectionListPage = 1;
        SearchInput.Text = searchQuery = ""; UpdateCollectionView(); await LoadPageAsync(1);
    }
    private async Task AddContextToLocalPlaylistAsync(MusicTrack track, Guid id)
    {
        if (track.SourceInstanceId == Guid.Empty) return;
        await WithContextPlaylistWriteAsync(async ct =>
        {
            var catalog = await playlistStore.LoadAsync(ct);
            var playlist = catalog.Playlists.FirstOrDefault(p => p.Id == id);
            if (playlist is null) { PlaybackStatus.Text = "歌单已不存在，请刷新后选择。"; return; }
            if (playlist.Entries.Any(entry => entry.Track == track.Reference)) { PlaybackStatus.Text = "该歌曲已在歌单中。"; return; }
            var updated = playlist with { Entries = playlist.Entries.Append(LocalPlaylistEntry.FromTrack(track)).ToArray() };
            var saved = await playlistStore.SaveAsync(catalog with { Playlists = catalog.Playlists.Select(p => p.Id == id ? updated : p).ToArray() }, ct);
            ct.ThrowIfCancellationRequested();
            RenderLocalPlaylistNavigation(saved, (LocalPlaylistNavigation.SelectedItem as LocalPlaylist)?.Id);
            PlaybackStatus.Text = "已加入本地歌单。";
        });
    }
    private async Task AddContextToNasPlaylistAsync(MusicTrack track, PlaylistReference reference)
    {
        var api = ContextClient;
        if (api is null || reference.SourceInstanceId != track.SourceInstanceId || api.SourceInstanceId != track.SourceInstanceId
            || !TrackSupports(track, MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists)) return;
        await WithContextPlaylistWriteAsync(async ct =>
        {
            var snapshot = await api.ReadPlaylistSnapshotAsync(reference, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentPlaylistClient(api)) return;
            await api.ChangePlaylistTracksAsync(snapshot, [track.Reference], true, ct);
            ct.ThrowIfCancellationRequested();
            if (IsCurrentPlaylistClient(api)) PlaybackStatus.Text = "已加入飞牛歌单并回读核对。";
        });
    }
    private async Task WithContextPlaylistWriteAsync(Func<CancellationToken, Task> operation)
    {
        var view = queuePage ?? fullPageView ?? activePlaylistPage;
        if (contextPlaylistWriteBusy || view is { BlockNavigation: true } or { Editor.IsEnabled: false }
            || activePlaylistPage is { BlockNavigation: true } || fullPageView is { BlockNavigation: true }) return;
        bool wasEnabled = view?.Editor.IsEnabled ?? true;
        contextPlaylistWriteBusy = true;
        if (view is not null) { view.BlockNavigation = true; view.Editor.IsEnabled = false; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(localLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await operation(timeout.Token); }
        finally
        {
            contextPlaylistWriteBusy = false;
            if (view is not null && !view.IsDisposed) { view.BlockNavigation = false; view.Editor.IsEnabled = wasEnabled; }
        }
        if (!closed && view?.Reload is { } reload && (ReferenceEquals(activePlaylistPage, view) || ReferenceEquals(fullPageView, view) || ReferenceEquals(queuePage, view))) await reload();
    }
}
