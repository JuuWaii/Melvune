using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Melvune.App;

public sealed partial class MainWindow
{
#if DEBUG
    private Action<ContentDialog>? playlistDeleteDialogOpened;
#endif
    private void PlaylistNavigation_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not ListView list || closed || !list.IsEnabled) return;
        bool pointer = args.TryGetPosition(list, out var position);
        var origin = pointer ? args.OriginalSource as DependencyObject : FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        var item = ResolveContextItem(list, origin, !pointer);
        if (item is not LocalPlaylist and not NasPlaylist) return;
        list.SelectedItem = item;
        ShowContextMenu(CreatePlaylistNavigationMenu(item), list, pointer ? position : null);
        args.Handled = true;
    }

    private MenuFlyout CreatePlaylistNavigationMenu(object playlist)
    {
        var menu = new MenuFlyout();
        bool available = CanOpenPlaylistNameDialog();
        void Add(string text, string tag, bool enabled, Func<Task> operation)
        {
            var command = new MenuFlyoutItem { Text = text, Tag = tag, IsEnabled = enabled };
            command.Click += async (_, _) => await RunContextCommandAsync(operation);
            menu.Items.Add(command);
        }
        if (playlist is LocalPlaylist local)
        {
            Add("打开歌单", "open", available, () => ShowLocalPlaylistsAsync(local.Id));
            Add("重命名", "rename", available, () => RenameLocalPlaylistAsync(local));
            Add("删除歌单…", "delete", available, () => DeleteLocalPlaylistAsync(local));
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("新建本地歌单…", "new", available, () => ShowLocalPlaylistsAsync(createNew: true));
        }
        else if (playlist is NasPlaylist remote)
        {
            var api = ContextClient;
            bool current = api is not null && IsCurrentPlaylistClient(api) && remote.Reference.SourceInstanceId == api.SourceInstanceId;
            bool read = current && ViewModel.PlaybackRouter.Supports(remote.Reference.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists);
            bool write = read && ViewModel.PlaybackRouter.Supports(remote.Reference.SourceInstanceId, MusicSourceCapabilities.WritePlaylists);
            Add("打开歌单", "open", available && read, async () => { if (api is not null && IsCurrentPlaylistClient(api)) await ShowNasPlaylistsAsync(api, remote.Reference); });
            Add("重命名", "rename", available && write, async () => { if (api is not null) await RenameNasPlaylistAsync(api, remote); });
            Add("删除歌单…", "delete", available && write, async () => { if (api is not null) await DeleteNasPlaylistAsync(api, remote); });
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("新建飞牛歌单…", "new", available && write, async () => { if (api is not null) await ShowNasPlaylistsAsync(api, createNew: true); });
        }
        ApplyMenuTheme(menu);
        return menu;
    }

    private static bool SameLocalPlaylist(LocalPlaylist? current, LocalPlaylist expected) => current is not null
        && current.Id == expected.Id && current.Name == expected.Name && current.Entries.SequenceEqual(expected.Entries);

    private async Task RenameLocalPlaylistAsync(LocalPlaylist expected)
    {
        var name = await RequestPlaylistNameAsync(false, expected.Name);
        if (name is null || closed) return;
        await WithContextPlaylistWriteAsync(async ct =>
        {
            var catalog = await playlistStore.LoadAsync(ct);
            if (!SameLocalPlaylist(catalog.Playlists.FirstOrDefault(item => item.Id == expected.Id), expected)) throw new PlaylistConflictException();
            var saved = await playlistStore.SaveAsync(catalog with { Playlists = catalog.Playlists.Select(item => item.Id == expected.Id ? item with { Name = name } : item).ToArray() }, ct);
            ct.ThrowIfCancellationRequested();
            RenderLocalPlaylistNavigation(saved, (LocalPlaylistNavigation.SelectedItem as LocalPlaylist)?.Id);
            PlaybackStatus.Text = "歌单已重命名。";
        });
    }

    private async Task<bool> ConfirmDeletePlaylistAsync(string name, int count, bool remote)
    {
        if (!CanOpenPlaylistNameDialog()) return false;
        CloseTrackContextMenu();
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "删除歌单",
            Content = new TextBlock { Text = $"删除“{name}”（{count} 首）？\n" + (remote ? "此操作同步到飞牛服务器。音乐文件保持。" : "只删除本机歌单，音乐文件保持。"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
#if DEBUG
        dialog.Opened += (_, _) => playlistDeleteDialogOpened?.Invoke(dialog);
#endif
        return await ShowThemedDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task DeleteLocalPlaylistAsync(LocalPlaylist expected)
    {
        if (!await ConfirmDeletePlaylistAsync(expected.Name, expected.Entries.Count, false) || closed) return;
        await WithContextPlaylistWriteAsync(async ct =>
        {
            var catalog = await playlistStore.LoadAsync(ct);
            if (!SameLocalPlaylist(catalog.Playlists.FirstOrDefault(item => item.Id == expected.Id), expected)) throw new PlaylistConflictException();
            var saved = await playlistStore.SaveAsync(catalog with { Playlists = catalog.Playlists.Where(item => item.Id != expected.Id).ToArray() }, ct);
            ct.ThrowIfCancellationRequested();
            RenderLocalPlaylistNavigation(saved);
            PlaybackStatus.Text = "歌单已删除，音乐文件保持。";
        });
    }

    private bool CanWriteNasPlaylist(NasApiClient api, PlaylistReference reference) => !closed && IsCurrentPlaylistClient(api)
        && reference.SourceInstanceId == api.SourceInstanceId
        && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists);

    private async Task WithNasPlaylistWriteAsync(NasApiClient api, Func<CancellationToken, Task> operation)
    {
        try { await WithContextPlaylistWriteAsync(operation); }
        catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
        {
            if (!closed && ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync();
            throw;
        }
        finally { metadataSnapshot = null; RequestMetadataSync(refresh: true); }
    }

    private async Task RenameNasPlaylistAsync(NasApiClient api, NasPlaylist expected)
    {
        if (!CanWriteNasPlaylist(api, expected.Reference)) return;
        var name = await RequestPlaylistNameAsync(true, expected.Name);
        if (name is null || !CanWriteNasPlaylist(api, expected.Reference)) return;
        await WithNasPlaylistWriteAsync(api, async ct =>
        {
            if (!CanWriteNasPlaylist(api, expected.Reference)) return;
            await api.RenamePlaylistAsync(expected, name, ct);
            var playlists = await api.ListPlaylistsAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!CanWriteNasPlaylist(api, expected.Reference)) return;
            RenderNasPlaylistNavigation(api, playlists, (NasPlaylistNavigation.SelectedItem as NasPlaylist)?.Reference);
            PlaybackStatus.Text = "飞牛歌单已重命名并回读核对。";
        });
    }

    private async Task DeleteNasPlaylistAsync(NasApiClient api, NasPlaylist expected)
    {
        if (!CanOpenPlaylistNameDialog() || !CanWriteNasPlaylist(api, expected.Reference)) return;
        int version = contentNavigationVersion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(localLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        NasPlaylistSnapshot snapshot;
        try { snapshot = await api.ReadPlaylistSnapshotAsync(expected.Reference, timeout.Token); }
        catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
        {
            if (!closed && ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync();
            throw;
        }
        if (!CanWriteNasPlaylist(api, expected.Reference) || version != contentNavigationVersion) return;
        if (snapshot.Playlist != expected) throw new RemotePlaylistConflictException();
        if (!await ConfirmDeletePlaylistAsync(expected.Name, snapshot.Tracks.Count, true) || !CanWriteNasPlaylist(api, expected.Reference)) return;
        await WithNasPlaylistWriteAsync(api, async ct =>
        {
            if (!CanWriteNasPlaylist(api, expected.Reference)) return;
            await api.DeletePlaylistAsync(snapshot, ct);
            var playlists = await api.ListPlaylistsAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!CanWriteNasPlaylist(api, expected.Reference)) return;
            RenderNasPlaylistNavigation(api, playlists);
            PlaybackStatus.Text = "飞牛歌单已删除并回读核对，音乐文件保持。";
        });
    }
}
