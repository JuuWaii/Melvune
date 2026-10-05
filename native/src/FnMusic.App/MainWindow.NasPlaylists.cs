using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;
public sealed partial class MainWindow
{
#if DEBUG
    private Infrastructure.NasApiClient? playlistTestClient;
    private Action<PlaylistPageView>? nasPlaylistPageOpened;
#endif
    private bool IsCurrentPlaylistClient(Infrastructure.NasApiClient api) => ReferenceEquals(api, ViewModel.AuthenticatedClient)
#if DEBUG
        || (IsSyntheticPreview && ReferenceEquals(api, playlistTestClient))
#endif
        ;
    private async void NasPlaylists_Click(object sender, RoutedEventArgs e)
    {
        if (closed) return;
        if (!RequireRemoteCapability(MusicSourceCapabilities.ReadPlaylists)) return;
        var api = ViewModel.AuthenticatedClient;
        if (api is null) { PlaybackStatus.Text = "请先连接飞牛并登录，再打开飞牛歌单。"; return; }
        try { await ShowPlaylistManagerAsync(true, api); }
        catch (OperationCanceledException) { }
        catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
        { if (!closed && ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync(); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "飞牛歌单加载失败，请检查连接后重试。"; }
    }

    private async Task ShowNasPlaylistsAsync(Infrastructure.NasApiClient api, PlaylistReference? selectedPlaylist = null, bool createNew = false, MusicTrack? pickedTrack = null)
    {
        IReadOnlyList<NasPlaylist> playlists = [];
        if (closed || !IsCurrentPlaylistClient(api)) return;
        if (createNew)
        {
            int version = contentNavigationVersion;
            var created = await CreateNasPlaylistFromDialogAsync(api);
            if (created is null || closed || version != contentNavigationVersion || !IsCurrentPlaylistClient(api)) return;
            selectedPlaylist = created;
        }
        var picked = pickedTrack ?? TrackList.SelectedItem as MusicTrack;
        NasPlaylistSnapshot? snapshot = null;
        bool busy = false;
        PlaylistReference? currentReference = selectedPlaylist;
        var list = new ListView { Height = 200, SelectionMode = ListViewSelectionMode.Single };
        ConfigureSongList(list);
        list.ContextRequested += TrackList_ContextRequested;
        list.ContextCanceled += TrackContext_Canceled;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 10, MinWidth = 300 };
        panel.Children.Add(new TextBlock { Text = "同步当前飞牛账号。编辑前检查服务端状态，写入后回读；网页端修改后点击刷新。最多管理 10000 首的歌单。", TextWrapping = TextWrapping.Wrap });
        var refresh = new Button { Content = "刷新" };
        var add = new Button { Content = "加入列表中所选飞牛歌曲" }; var remove = new Button { Content = "移除所选歌曲" };
        panel.Children.Add(add);
        var entryButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        entryButtons.Children.Add(remove); entryButtons.Children.Add(refresh);
        var view = new PlaylistPageView("飞牛歌单", panel, list, entryButtons, status, localLifetime.Token) { RemoteSource = api.SourceInstanceId };
        var editor = view.Editor;
        var ct = view.Lifetime.Token;
        void UpdateButtons()
        {
            bool selected = snapshot is not null;
            bool writable = IsCurrentPlaylistClient(api) && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists);
            add.IsEnabled = writable && selected && picked is not null && picked.SourceInstanceId == api.SourceInstanceId;
            remove.IsEnabled = writable && selected && list.SelectedItem is MusicTrack;
            view.PlayButton.IsEnabled = !busy && selected && list.SelectedItem is MusicTrack { IsCue: false } song && ViewModel.PlaybackRouter.CanPlay(song.Reference);
        }
        void RenderSnapshot()
        {
            view.TitleText.Text = snapshot?.Playlist.Name ?? "飞牛歌单";
            view.RemotePlaylistReference = snapshot?.Playlist.Reference;
            list.ItemsSource = snapshot?.Tracks; list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
            UpdateButtons();
            if (snapshot is { } current) playlists = playlists.Select(item => item.Reference == current.Playlist.Reference ? current.Playlist : item).ToArray();
            if (ReferenceEquals(activePlaylistPage, view)) RenderNasPlaylistNavigation(api, playlists, snapshot?.Playlist.Reference);
        }
        async Task LoadSelected()
        {
            snapshot = null; RenderSnapshot();
            if (currentReference is { } selected) snapshot = await api.ReadPlaylistSnapshotAsync(selected, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentPlaylistClient(api)) throw new OperationCanceledException(ct);
            RenderSnapshot();
        }
        async Task Refresh(PlaylistReference? selected = null)
        {
            playlists = await api.ListPlaylistsAsync(ct); ct.ThrowIfCancellationRequested();
            if (!IsCurrentPlaylistClient(api)) throw new OperationCanceledException(ct);
            currentReference = (playlists.FirstOrDefault(item => item.Reference == selected) ?? playlists.FirstOrDefault())?.Reference;
            await LoadSelected();
            if (ReferenceEquals(activePlaylistPage, view)) RenderNasPlaylistNavigation(api, playlists, currentReference);
        }
        async Task Run(Func<Task> operation, bool mutation = false)
        {
            if (busy || ct.IsCancellationRequested || !IsCurrentPlaylistClient(api)) return;
            var required = MusicSourceCapabilities.ReadPlaylists | (mutation ? MusicSourceCapabilities.WritePlaylists : MusicSourceCapabilities.None);
            if (!ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, required))
            { status.Text = "当前来源不支持歌单" + (mutation ? "写入。" : "读取。"); UpdateButtons(); return; }
            busy = true; view.BlockNavigation = mutation; editor.IsEnabled = false; view.PlayButton.IsEnabled = false;
            status.Text = "正在同步飞牛歌单…";
            try { await operation(); ct.ThrowIfCancellationRequested(); status.Text = "已与飞牛服务器核对。"; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (RemotePlaylistConflictException)
            { snapshot = null; RenderSnapshot(); status.Text = "服务端歌单已变化或结果不一致，请刷新核对后重试。"; }
            catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
            { if (ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync(); }
            catch (Exception)
            { snapshot = null; RenderSnapshot(); status.Text = "未能确认同步结果，请先刷新核对，避免重复创建或修改。"; }
            finally
            {
                busy = view.BlockNavigation = false; editor.IsEnabled = true; UpdateButtons();
            }
        }
        list.SelectionChanged += (_, _) => UpdateButtons();
        add.Click += async (_, _) =>
        {
            if (snapshot is not { } current || picked is null || picked.SourceInstanceId != api.SourceInstanceId) return;
            await Run(async () => { snapshot = await api.ChangePlaylistTracksAsync(current, [picked.Reference], true, ct); ct.ThrowIfCancellationRequested(); RenderSnapshot(); }, true);
        };
        remove.Click += async (_, _) =>
        {
            if (snapshot is not { } current || list.SelectedItem is not MusicTrack track) return;
            await Run(async () => { snapshot = await api.ChangePlaylistTracksAsync(current, [track.Reference], false, ct); ct.ThrowIfCancellationRequested(); RenderSnapshot(); }, true);
        };
        refresh.Click += async (_, _) => await Run(() => Refresh(currentReference));
        view.Reload = () => Run(() => Refresh(currentReference));
        view.PlayButton.Click += async (_, _) =>
        {
            if (ct.IsCancellationRequested || closed || !IsCurrentPlaylistClient(api) || snapshot is not { } selected || list.SelectedItem is not MusicTrack { IsCue: false } start) return;
            if (queue.Replace(selected.Tracks, start.Reference))
            { queueOrigin = "飞牛歌单 · " + selected.Playlist.Name; await PlayTrackAsync(start); }
        };
        if (!await MountPlaylistPageAsync(view)) return;
        if (!IsCurrentPlaylistClient(api)) { if (ReferenceEquals(activePlaylistPage, view)) ClosePlaylistPage(); return; }
        await Run(() => Refresh(selectedPlaylist));
        if (ct.IsCancellationRequested || closed || !IsCurrentPlaylistClient(api)) return;
#if DEBUG
        if (ReferenceEquals(activePlaylistPage, view)) nasPlaylistPageOpened?.Invoke(view);
#endif
    }
}
