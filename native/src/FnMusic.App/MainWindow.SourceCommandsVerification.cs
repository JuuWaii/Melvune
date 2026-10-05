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
            verify("unregistered_remote_navigation_disabled", !RemoteAlbumsNavigation.IsEnabled && !RemoteFavoritesNavigation.IsEnabled && AllMusicNavigation.IsEnabled
                && !RemotePlaylistsNavigation.IsEnabled && !RemoteSettingsNavigation.IsEnabled);
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(id, MusicSourceCapabilities.Browse));
            ConnectionCapabilitiesChanged(ViewModel, new(nameof(ViewModel.AuthenticatedClient)));
            verify("read_only_browse_navigation", RemoteAlbumsNavigation.IsEnabled && RemoteArtistsNavigation.IsEnabled
                && !RemoteFavoritesNavigation.IsEnabled && !RemotePlaylistsNavigation.IsEnabled && !RemoteSettingsNavigation.IsEnabled
                && !SearchInput.IsEnabled && !SearchAction.IsEnabled);
            await SwitchCollectionAsync(null);
            var items = TrackList.ItemsSource;
            await ShowFavoritesAsync();
            verify("unsupported_favorites_preserve_browser", ReferenceEquals(items, TrackList.ItemsSource) && !favoritesView && !string.IsNullOrEmpty(PlaybackStatus.Text));
            searchQuery = "不支持搜索";
            await LoadPageAsync(1);
            verify("unsupported_search_preserve_browser", ReferenceEquals(items, TrackList.ItemsSource));
            searchQuery = "";
            await SwitchCollectionAsync(CollectionKind.Album);
            await LoadPageAsync(2);
            await OpenCollectionAsync(((IEnumerable<MusicCollection>)CollectionList.ItemsSource!).First());
            var detailItems = TrackList.ItemsSource;
            var detailSelection = TrackList.SelectedItem;
            var detail = collection;
            int detailPage = page, detailTotal = total;
            SearchInput.Text = "不支持搜索";
            await SearchAsync();
            verify("unsupported_search_entry_preserves_detail_context", ReferenceEquals(detailItems, TrackList.ItemsSource)
                && ReferenceEquals(detailSelection, TrackList.SelectedItem) && ReferenceEquals(detail, collection)
                && collectionKind == CollectionKind.Album && page == detailPage && total == detailTotal
                && searchQuery.Length == 0 && TrackList.Visibility == Visibility.Visible);
            await SwitchCollectionAsync(null);
            items = TrackList.ItemsSource;
            ViewModel.PlaybackRouter.Unregister(id);
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(id, MusicSourceCapabilities.Browse | MusicSourceCapabilities.ReadFavorites
                | MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.ReadSettings));
            UpdateSourceCommands();
            verify("read_capability_navigation_enabled", RemoteFavoritesNavigation.IsEnabled
                && RemotePlaylistsNavigation.IsEnabled && RemoteSettingsNavigation.IsEnabled);
            await ShowFavoritesAsync();
            var favoriteItems = TrackList.ItemsSource;
            string favoritePageStatus = PageStatus.Text;
            SearchInput.Text = "不支持搜索";
            await SearchAsync();
            verify("unsupported_search_entry_preserves_favorites", favoritesView && ReferenceEquals(favoriteItems, TrackList.ItemsSource)
                && searchQuery.Length == 0 && PageStatus.Text == favoritePageStatus);
            await SwitchCollectionAsync(null);
            items = TrackList.ItemsSource;
            ViewModel.PlaybackRouter.Unregister(id);
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(id, MusicSourceCapabilities.Search));
            UpdateSourceCommands();
            SearchInput.Text = "合成";
            await SearchAsync();
            var searchItems = TrackList.ItemsSource;
            string currentQuery = searchQuery, searchPageStatus = PageStatus.Text;
            SearchInput.Text = " ";
            await SearchAsync();
            verify("empty_search_requires_browse_preserves_search_results", currentQuery == "合成" && searchQuery == currentQuery
                && ReferenceEquals(searchItems, TrackList.ItemsSource) && PageStatus.Text == searchPageStatus
                && SearchAction.IsEnabled && !RemoteAlbumsNavigation.IsEnabled);
            ViewModel.PlaybackRouter.Unregister(id);
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(id, MusicSourceCapabilities.Browse));
            await SwitchCollectionAsync(null);
            items = TrackList.ItemsSource;
            ViewModel.PlaybackRouter.Unregister(id);
            ConnectionCapabilitiesChanged(ViewModel, new(nameof(ViewModel.AuthenticatedClient)));
            verify("disconnected_remote_navigation_disabled", !RemoteAlbumsNavigation.IsEnabled && !RemoteFavoritesNavigation.IsEnabled && AllMusicNavigation.IsEnabled
                && !RemotePlaylistsNavigation.IsEnabled && !RemoteSettingsNavigation.IsEnabled);
            var panel = LibraryPanel.Visibility;
            await ShowLibraryAsync();
            verify("disconnected_browse_rejected_before_navigation", ReferenceEquals(items, TrackList.ItemsSource) && LibraryPanel.Visibility == panel);
            SearchInput.Text = "无法搜索";
            await SearchAsync();
            verify("disconnected_search_entry_preserves_browser", ReferenceEquals(items, TrackList.ItemsSource)
                && searchQuery.Length == 0 && !favoritesView && collection is null && LibraryPanel.Visibility == panel);
        }
        finally { ViewModel.PlaybackRouter.Unregister(id); remoteCapabilityTestSource = null; searchQuery = ""; UpdateSourceCommands(); }
    }
}
#endif
