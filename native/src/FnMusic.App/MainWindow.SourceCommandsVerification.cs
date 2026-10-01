#if DEBUG
using FnMusic.Core;
using Microsoft.UI.Xaml;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private sealed class RemoteCapabilityFixture(Guid id, MusicSourceCapabilities capabilities) : IMusicPlaybackSource
    {
        public MusicSource Source { get; } = new(id, "能力夹具", "fixture", capabilities);
        public Task<Stream> OpenAsync(TrackReference track, CancellationToken ct) => throw new InvalidOperationException();
    }
    private async Task VerifyRemoteCommandsWindowAsync(Action<string, bool> verify)
    {
        Guid id = Guid.NewGuid(); remoteCapabilityTestSource = id;
        try
        {
            UpdateSourceCommands();
            verify("unregistered_remote_navigation_disabled", !RemoteMusicNavigation.IsEnabled && !RemoteFavoritesNavigation.IsEnabled
                && !RemotePlaylistsNavigation.IsEnabled && !RemoteSettingsNavigation.IsEnabled);
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(id, MusicSourceCapabilities.Browse));
            ConnectionCapabilitiesChanged(ViewModel, new(nameof(ViewModel.AuthenticatedClient)));
            verify("read_only_browse_navigation", RemoteMusicNavigation.IsEnabled && RemoteAlbumsNavigation.IsEnabled && RemoteArtistsTab.IsEnabled
                && !RemoteFavoritesTab.IsEnabled && !RemotePlaylistsNavigation.IsEnabled && !RemoteSettingsNavigation.IsEnabled
                && !SearchInput.IsEnabled && !SearchAction.IsEnabled);
            await SwitchCollectionAsync(null);
            var items = TrackList.ItemsSource;
            await ShowFavoritesAsync();
            verify("unsupported_favorites_preserve_browser", ReferenceEquals(items, TrackList.ItemsSource) && !favoritesView && !string.IsNullOrEmpty(PlaybackStatus.Text));
            searchQuery = "不支持搜索";
            await LoadPageAsync(1);
            verify("unsupported_search_preserve_browser", ReferenceEquals(items, TrackList.ItemsSource));
            searchQuery = "";
            ViewModel.PlaybackRouter.Unregister(id);
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(id, MusicSourceCapabilities.Browse | MusicSourceCapabilities.ReadFavorites
                | MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.ReadSettings));
            UpdateSourceCommands();
            verify("read_capability_navigation_enabled", RemoteFavoritesNavigation.IsEnabled && RemoteFavoritesTab.IsEnabled
                && RemotePlaylistsNavigation.IsEnabled && RemoteSettingsNavigation.IsEnabled);
            ViewModel.PlaybackRouter.Unregister(id);
            ConnectionCapabilitiesChanged(ViewModel, new(nameof(ViewModel.AuthenticatedClient)));
            verify("disconnected_remote_navigation_disabled", !RemoteMusicNavigation.IsEnabled && !RemoteFavoritesTab.IsEnabled
                && !RemotePlaylistsNavigation.IsEnabled && !RemoteSettingsNavigation.IsEnabled);
            var panel = LibraryPanel.Visibility;
            await ShowLibraryAsync();
            verify("disconnected_browse_rejected_before_navigation", ReferenceEquals(items, TrackList.ItemsSource) && LibraryPanel.Visibility == panel);
        }
        finally { ViewModel.PlaybackRouter.Unregister(id); remoteCapabilityTestSource = null; searchQuery = ""; UpdateSourceCommands(); }
    }
}
#endif
