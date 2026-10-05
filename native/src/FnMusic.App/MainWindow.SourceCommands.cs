using System.ComponentModel;
using FnMusic.Core;
using FnMusic.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
#if DEBUG
    private Guid? remoteCapabilityTestSource;
#endif
    private bool RemoteSupports(MusicSourceCapabilities capability)
    {
#if DEBUG
        if (IsSyntheticPreview && remoteCapabilityTestSource is { } fixture)
            return ViewModel.PlaybackRouter.Supports(fixture, capability);
        if (IsSyntheticPreview && ViewModel.AuthenticatedClient is null)
            return capability is MusicSourceCapabilities.Browse or MusicSourceCapabilities.Search or MusicSourceCapabilities.ReadFavorites;
#endif
        return ViewModel.AuthenticatedClient is { } api && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, capability);
    }
    private bool RequireRemoteCapability(MusicSourceCapabilities capability)
    {
        if (RemoteSupports(capability)) return true;
        PlaybackStatus.Text = ViewModel.AuthenticatedClient is null ? "请先连接飞牛并登录。" : "当前飞牛来源不支持此操作。";
        return false;
    }
    private void ConnectionCapabilitiesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ConnectionViewModel.AuthenticatedClient) or nameof(ConnectionViewModel.IsReady)) UpdateSourceCommands();
        if (args.PropertyName == nameof(ConnectionViewModel.AuthenticatedClient) && !closed) _ = RefreshPlaylistNavigationAsync();
    }
    private void UpdateSourceCommands()
    {
        if (closed) return;
        CloseTrackContextMenu();
        void Set(Button button, MusicSourceCapabilities capability)
        {
            button.IsEnabled = RemoteSupports(capability);
            ToolTipService.SetToolTip(button, button.IsEnabled ? null : ViewModel.AuthenticatedClient is null
                ? "连接飞牛并登录后使用；本地音乐和歌单可独立使用。" : "当前来源不支持此操作。");
        }
        AllMusicNavigation.IsEnabled = true;
        foreach (var button in new[] { RemoteAlbumsNavigation, RemoteArtistsNavigation, RemoteSongsTab, RemoteAlbumsTab, RemoteArtistsTab }) Set(button, MusicSourceCapabilities.Browse);
        if (allMusicView) RemoteSongsTab.IsEnabled = true;
        foreach (var button in new[] { RemoteFavoritesNavigation, RemoteFavoritesTab }) Set(button, MusicSourceCapabilities.ReadFavorites);
        Set(RemotePlaylistsNavigation, MusicSourceCapabilities.ReadPlaylists);
        Set(NewNasPlaylist, MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists);
        NasPlaylistNavigation.IsEnabled = RemoteSupports(MusicSourceCapabilities.ReadPlaylists);
        Set(RemoteSettingsNavigation, MusicSourceCapabilities.ReadSettings);
        SearchInput.IsEnabled = SearchAction.IsEnabled = allMusicView || localView || RemoteSupports(MusicSourceCapabilities.Search);
        ToolTipService.SetToolTip(SearchAction, SearchAction.IsEnabled ? null : "当前来源未连接或不支持搜索。");
        UpdateFavoriteButton();
        activePlaylistPage?.SourceChanged?.Invoke();
        fullPageView?.SourceChanged?.Invoke();
        queuePage?.SourceChanged?.Invoke();
    }
}
