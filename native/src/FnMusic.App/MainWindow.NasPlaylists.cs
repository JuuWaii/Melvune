using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private bool nasPlaylistDialogOpen;
#if DEBUG
    private Infrastructure.NasApiClient? playlistTestClient;
    private Action<ContentDialog>? nasPlaylistDialogOpened;
#endif
    private bool IsCurrentPlaylistClient(Infrastructure.NasApiClient api) => ReferenceEquals(api, ViewModel.AuthenticatedClient)
#if DEBUG
        || (IsSyntheticPreview && ReferenceEquals(api, playlistTestClient))
#endif
        ;
    private async void NasPlaylists_Click(object sender, RoutedEventArgs e)
    {
        if (nasPlaylistDialogOpen || closed) return;
        var api = ViewModel.AuthenticatedClient;
        if (api is null) { PlaybackStatus.Text = "请先连接飞牛并登录，再打开飞牛歌单。"; return; }
        nasPlaylistDialogOpen = true;
        try { await ShowNasPlaylistsAsync(api); }
        catch (OperationCanceledException) { }
        catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
        { if (!closed && ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync(); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "飞牛歌单加载失败，请检查连接后重试。"; }
        finally { nasPlaylistDialogOpen = false; }
    }

    private async Task ShowNasPlaylistsAsync(Infrastructure.NasApiClient api)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(localLifetime.Token);
        var ct = lifetime.Token;
        var playlists = await api.ListPlaylistsAsync(ct);
        if (closed || !IsCurrentPlaylistClient(api)) return;
        var picked = TrackList.SelectedItem as MusicTrack;
        NasPlaylistSnapshot? snapshot = null;
        bool busy = false, rendering = false, showing = false;
        var picker = new ComboBox { Header = "选择飞牛歌单", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "歌单名称", MaxLength = 32, PlaceholderText = "输入名称后新建或改名" };
        var list = new ListView { DisplayMemberPath = "Title", Height = 200, SelectionMode = ListViewSelectionMode.Single };
        var confirm = new CheckBox { Content = "确认从飞牛服务器删除所选歌单" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 10, MinWidth = 300 };
        panel.Children.Add(new TextBlock { Text = "同步当前飞牛账号。编辑前检查服务端状态，写入后回读；网页端修改后点击刷新。最多管理 10000 首的歌单。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(picker); panel.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var create = new Button { Content = "新建" }; var rename = new Button { Content = "改名" };
        var delete = new Button { Content = "删除歌单" }; var refresh = new Button { Content = "刷新" };
        foreach (var button in new[] { create, rename, delete, refresh }) actions.Children.Add(button);
        panel.Children.Add(actions); panel.Children.Add(confirm);
        var add = new Button { Content = "加入列表中所选飞牛歌曲" }; var remove = new Button { Content = "移除所选歌曲" };
        panel.Children.Add(add); panel.Children.Add(list); panel.Children.Add(remove); panel.Children.Add(status);
        var editor = new ScrollViewer { Content = panel, MaxHeight = 520 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "飞牛歌单", Content = editor, PrimaryButtonText = "播放所选", CloseButtonText = "关闭" };
        void UpdateButtons()
        {
            bool selected = snapshot is not null;
            rename.IsEnabled = selected; delete.IsEnabled = selected && confirm.IsChecked == true;
            add.IsEnabled = selected && picked is not null && picked.SourceInstanceId == api.SourceInstanceId;
            remove.IsEnabled = selected && list.SelectedItem is MusicTrack;
            dialog.IsPrimaryButtonEnabled = !busy && selected && list.SelectedItem is MusicTrack { IsCue: false };
        }
        void RenderSnapshot()
        {
            name.Text = snapshot?.Playlist.Name ?? ""; confirm.IsChecked = false;
            list.ItemsSource = snapshot?.Tracks; list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
            UpdateButtons();
        }
        async Task LoadSelected()
        {
            snapshot = null; RenderSnapshot();
            if (picker.SelectedItem is NasPlaylist selected) snapshot = await api.ReadPlaylistSnapshotAsync(selected.Reference, ct);
            ct.ThrowIfCancellationRequested(); RenderSnapshot();
        }
        async Task Refresh(PlaylistReference? selected = null)
        {
            playlists = await api.ListPlaylistsAsync(ct); ct.ThrowIfCancellationRequested();
            rendering = true;
            picker.ItemsSource = playlists;
            picker.SelectedItem = playlists.FirstOrDefault(item => item.Reference == selected) ?? playlists.FirstOrDefault();
            rendering = false;
            await LoadSelected();
        }
        async Task Run(Func<Task> operation)
        {
            if (busy || ct.IsCancellationRequested) return;
            busy = true; editor.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false;
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
                busy = false; editor.IsEnabled = true; UpdateButtons();
                if (ct.IsCancellationRequested && showing && !closed) dialog.Hide();
            }
        }
        picker.SelectionChanged += async (_, _) => { if (!rendering) await Run(LoadSelected); };
        list.SelectionChanged += (_, _) => UpdateButtons(); confirm.Click += (_, _) => UpdateButtons();
        create.Click += async (_, _) => await Run(async () =>
        {
            var created = await api.CreatePlaylistAsync(name.Text, ct);
            await Refresh(created.Reference);
        });
        rename.Click += async (_, _) =>
        {
            if (snapshot is not { } current) return;
            await Run(async () => { var renamed = await api.RenamePlaylistAsync(current.Playlist, name.Text, ct); await Refresh(renamed.Reference); });
        };
        delete.Click += async (_, _) =>
        {
            if (snapshot is not { } current || confirm.IsChecked != true) return;
            await Run(async () => { await api.DeletePlaylistAsync(current, ct); await Refresh(); });
        };
        add.Click += async (_, _) =>
        {
            if (snapshot is not { } current || picked is null || picked.SourceInstanceId != api.SourceInstanceId) return;
            await Run(async () => { snapshot = await api.ChangePlaylistTracksAsync(current, [picked.Reference], true, ct); RenderSnapshot(); });
        };
        remove.Click += async (_, _) =>
        {
            if (snapshot is not { } current || list.SelectedItem is not MusicTrack track) return;
            await Run(async () => { snapshot = await api.ChangePlaylistTracksAsync(current, [track.Reference], false, ct); RenderSnapshot(); });
        };
        refresh.Click += async (_, _) => await Run(() => Refresh((picker.SelectedItem as NasPlaylist)?.Reference));
        dialog.Closing += (_, args) => { if (busy && !ct.IsCancellationRequested && !closed) args.Cancel = true; };
        void SessionChanged() { lifetime.Cancel(); if (!busy && showing && !closed) dialog.Hide(); }
        ViewModel.SessionChanging += SessionChanged;
        try
        {
            await Refresh();
            if (closed || !IsCurrentPlaylistClient(api)) return;
#if DEBUG
            dialog.Opened += (_, _) => nasPlaylistDialogOpened?.Invoke(dialog);
#endif
            showing = true;
            var result = await ShowThemedDialogAsync(dialog);
            showing = false;
            if (result == ContentDialogResult.Primary && !ct.IsCancellationRequested && !closed && snapshot is { } selected && list.SelectedItem is MusicTrack start)
            {
                if (queue.Replace(selected.Tracks, start.Reference))
                { queueOrigin = "飞牛歌单 · " + selected.Playlist.Name; await PlayTrackAsync(start); }
            }
        }
        finally { ViewModel.SessionChanging -= SessionChanged; lifetime.Cancel(); }
    }
}
