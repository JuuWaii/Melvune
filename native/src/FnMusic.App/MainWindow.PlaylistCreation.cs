using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
#if DEBUG
    private Action<ContentDialog, TextBox>? playlistNameDialogOpened;
#endif
    private bool CanOpenPlaylistNameDialog()
    {
        // A full-page manager disables NormalContent, including an underlying detail editor.
        // Inspect readiness of the foreground page rather than inherited disabled controls below it.
        var foreground = queuePage ?? fullPageView ?? activePlaylistPage;
        return !closed && !themedDialogOpen && !contextPlaylistWriteBusy
            && foreground is not { BlockNavigation: true } and not { Editor.IsEnabled: false }
            && activePlaylistPage is not { BlockNavigation: true } && fullPageView is not { BlockNavigation: true }
            && queuePage is not { BlockNavigation: true };
    }

    private async Task<string?> RequestPlaylistNameAsync(bool remote, string? initialName = null)
    {
        if (!CanOpenPlaylistNameDialog()) return null;
        CloseTrackContextMenu();
        var name = new TextBox { Header = "歌单名称", PlaceholderText = "为歌单起个名字", MaxLength = remote ? 32 : 120, Text = initialName ?? "" };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = initialName is not null ? "重命名歌单" : remote ? "新建飞牛歌单" : "新建本地歌单",
            Content = name, PrimaryButtonText = initialName is null ? "创建" : "保存", CloseButtonText = "取消", IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text),
            DefaultButton = ContentDialogButton.Primary };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(name.Text)) args.Cancel = true; };
        dialog.Opened += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
#if DEBUG
            playlistNameDialogOpened?.Invoke(dialog, name);
#endif
        };
        return await ShowThemedDialogAsync(dialog) == ContentDialogResult.Primary ? name.Text.Trim() : null;
    }

    private async Task<Guid?> CreateLocalPlaylistFromDialogAsync()
    {
        if (!CanOpenPlaylistNameDialog()) return null;
        var catalog = await playlistStore.LoadAsync(localLifetime.Token);
        var name = await RequestPlaylistNameAsync(remote: false);
        if (name is null || closed) return null;
        Guid? createdId = null;
        await WithContextPlaylistWriteAsync(async ct =>
        {
            var created = new LocalPlaylist(Guid.NewGuid(), name, []);
            var saved = await playlistStore.SaveAsync(catalog with { Playlists = catalog.Playlists.Append(created).ToArray() }, ct);
            ct.ThrowIfCancellationRequested();
            RenderLocalPlaylistNavigation(saved, created.Id);
            PlaybackStatus.Text = "本地歌单已创建。";
            createdId = created.Id;
        });
        return createdId;
    }

    private async Task<PlaylistReference?> CreateNasPlaylistFromDialogAsync(NasApiClient api)
    {
        bool Writable() => !closed && IsCurrentPlaylistClient(api)
            && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists);
        if (!CanOpenPlaylistNameDialog() || !Writable()) return null;
        var name = await RequestPlaylistNameAsync(remote: true);
        if (name is null || !Writable()) return null;
        PlaylistReference? createdReference = null;
        await WithNasPlaylistWriteAsync(api, async ct =>
        {
            if (!Writable()) return;
            var created = await api.CreatePlaylistAsync(name, ct);
            var playlists = await api.ListPlaylistsAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!Writable()) return;
            RenderNasPlaylistNavigation(api, playlists, created.Reference);
            PlaybackStatus.Text = "飞牛歌单已创建并回读核对。";
            createdReference = created.Reference;
        });
        return createdReference;
    }
}
