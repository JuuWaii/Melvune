#if DEBUG
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private async Task VerifyTrackContextWindowAsync(Action<string, bool> verify)
    {
        await NavigateMainContentAsync(LibraryPanel);
        var track = localSources.Values.First().Tracks.First();
        var other = track with { Id = track.Id + "-other", Title = "菜单另一行" };
        // An unavailable source with the same provider ID must not be acted on accidentally.
        var foreign = track with { SourceInstanceId = Guid.NewGuid(), Title = "离线同 ID" };
        TrackList.ItemsSource = new[] { track, other, foreign }; TrackList.SelectedItem = track;
        Root.UpdateLayout(); TrackList.ScrollIntoView(other); await Task.Delay(50);
        var container = TrackList.ContainerFromItem(other) as ListViewItem ?? throw new InvalidOperationException();
        verify("context_pointer_resolves_hit_row_not_old_selection", ResolveContextItem(TrackList, container, false) is MusicTrack hit && hit.Reference == other.Reference);
        verify("context_pointer_empty_space_has_no_old_selection_fallback", ResolveContextItem(TrackList, TrackList, false) is null);
        verify("context_keyboard_uses_focused_row", ResolveContextItem(TrackList, container, true) is MusicTrack focused && focused.Reference == other.Reference);
        verify("context_keyboard_list_focus_uses_selection", ResolveContextItem(TrackList, TrackList, true) is MusicTrack selected && selected.Reference == track.Reference);
        var theme = Root.RequestedTheme;
        try
        {
            foreach (var mode in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                Root.RequestedTheme = mode;
                var themedMenu = CreateTrackContextMenu(TrackList, track, true);
                verify("context_menu_theme_" + mode.ToString().ToLowerInvariant(), themedMenu.MenuFlyoutPresenterStyle.Setters.OfType<Setter>()
                    .Any(s => s.Property == FrameworkElement.RequestedThemeProperty && Equals(s.Value, mode)));
            }
            ShowTrackContextMenu(TrackList, track, true); await Task.Delay(50);
            Root.RequestedTheme = ElementTheme.Light; await Task.Delay(80);
            verify("context_menu_theme_change_closes_old_presenter", trackContextMenu is null);
        }
        finally { CloseTrackContextMenu(); Root.RequestedTheme = theme; }
        var unavailable = CreateTrackContextMenu(TrackList, foreign, true);
        MenuFlyoutItem Item(MenuFlyout menu, string tag) => menu.Items.OfType<MenuFlyoutItem>().Single(i => Equals(i.Tag, tag));
        MenuFlyoutSubItem Sub(MenuFlyout menu, string tag) => menu.Items.OfType<MenuFlyoutSubItem>().Single(i => Equals(i.Tag, tag));
        async Task InvokeSubmenu(MenuFlyout flyout, MenuFlyoutSubItem submenu, MenuFlyoutItem command)
        {
            flyout.ShowAt(TrackList); await Task.Delay(50);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(submenu) ?? throw new InvalidOperationException();
            ((IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse)).Expand();
            await Task.Delay(50);
            ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(command).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(100);
            for (int i = 0; i < 100 && contextPlaylistWriteBusy; i++) await Task.Delay(50);
            flyout.Hide();
        }
        verify("context_offline_play_next_disabled", !Item(unavailable, "play").IsEnabled && !Item(unavailable, "next").IsEnabled);
        verify("context_offline_remote_commands_disabled", !Item(unavailable, "favorite").IsEnabled && !Sub(unavailable, "artists").IsEnabled
            && !Item(unavailable, "album").IsEnabled && !Sub(unavailable, "nas-playlists").IsEnabled);
        verify("context_offline_local_reference_can_be_saved", Sub(unavailable, "local-playlists").IsEnabled);
        var cue = CreateTrackContextMenu(TrackList, track with { IsCue = true }, true);
        verify("context_cue_play_next_disabled", !Item(cue, "play").IsEnabled && !Item(cue, "next").IsEnabled);
        TrackList.ItemsSource = new[] { track }; TrackList.SelectedItem = track;
        await StartQueueAsync(track);
        for (int i = 0; i < 100 && !mediaReady; i++) await Task.Delay(20);
        Pause_Click(this, new RoutedEventArgs());
        var audio = music.Player.Source; var current = queue.Current?.Reference;
        var playing = music.Player.PlaybackSession.PlaybackState;
        var added = track with { SourceInstanceId = Guid.Empty, Id = "context-next", Title = "菜单下一首" };
        TrackList.ItemsSource = new[] { track, added }; TrackList.SelectedItem = track;
        var menu = CreateTrackContextMenu(TrackList, added, true);
        menu.ShowAt(TrackList); await Task.Delay(50);
        ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(Item(menu, "next")).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(80); menu.Hide();
        verify("context_next_menu_command_targets_captured_track", queue.Count == 2 && queue.Tracks[1].Reference == added.Reference && queue.Current?.Reference == current);
        verify("context_next_menu_command_preserves_audio_state", ReferenceEquals(music.Player.Source, audio) && mediaReady && music.Player.PlaybackSession.PlaybackState == playing);
        verify("context_current_track_cannot_requeue_itself", !Item(CreateTrackContextMenu(TrackList, track, true), "next").IsEnabled);
        var stored = await playlistStore.LoadAsync();
        var target = new LocalPlaylist(Guid.NewGuid(), "菜单歌单", []);
        var saved = await playlistStore.SaveAsync(stored with { Playlists = [target] });
        RenderLocalPlaylistNavigation(saved);
        menu = CreateTrackContextMenu(TrackList, foreign, true);
        var local = Sub(menu, "local-playlists");
        verify("context_local_playlist_submenu_has_named_destination", local.Items.OfType<MenuFlyoutItem>().Any(i => Equals(i.Tag, target.Id.ToString())));
        await InvokeSubmenu(menu, local, local.Items.OfType<MenuFlyoutItem>().Single(i => Equals(i.Tag, target.Id.ToString())));
        var catalog = await playlistStore.LoadAsync();
        verify("context_local_add_keeps_clicked_source_and_snapshot", catalog.Playlists.Single().Entries.Single().Track == foreign.Reference);
        long revision = catalog.Revision;
        await AddContextToLocalPlaylistAsync(foreign, target.Id);
        verify("context_local_add_deduplicates_without_extra_write", (await playlistStore.LoadAsync()).Revision == revision);
        await AddContextToLocalPlaylistAsync(track, target.Id);
        verify("context_local_add_same_id_separate_source_preserved", (await playlistStore.LoadAsync()).Playlists.Single().Entries.Count == 2);
        await ShowLocalPlaylistsAsync(target.Id, picked: track);
        var view = activePlaylistPage ?? throw new InvalidOperationException();
        var unknown = CreateTrackContextMenu(view.List, ContextTrack(view.List.Items[0])!, false);
        verify("context_playlist_snapshot_unknown_favorite_is_disabled", !Item(unknown, "favorite").IsEnabled);
        var newcomer = foreign with { Id = "new-offline", Title = "新增离线引用" };
        await AddContextToLocalPlaylistAsync(newcomer, target.Id);
        verify("context_local_add_refreshes_open_page", view.List.Items.Count == 3 && ReferenceEquals(activePlaylistPage, view));
        view.BlockNavigation = true;
        try
        {
            revision = (await playlistStore.LoadAsync()).Revision;
            await AddContextToLocalPlaylistAsync(track with { Id = "busy" }, target.Id);
            verify("context_local_add_rejects_overlapping_page_mutation", (await playlistStore.LoadAsync()).Revision == revision);
        }
        finally { view.BlockNavigation = false; }
        await NavigateMainContentAsync(LibraryPanel);
        var synthetic = track with { SourceInstanceId = Guid.Empty, Id = "context-favorite", IsFavorite = false };
        var sameId = synthetic with { SourceInstanceId = Guid.NewGuid() };
        TrackList.ItemsSource = new[] { synthetic, sameId }; TrackList.SelectedItem = sameId;
        await ToggleFavoriteTrackAsync(synthetic);
        var changed = ((IEnumerable<MusicTrack>)TrackList.ItemsSource).ToArray();
        verify("context_favorite_changes_captured_reference_only", changed[0].IsFavorite && !changed[1].IsFavorite && ((MusicTrack)TrackList.SelectedItem).Reference == sameId.Reference);
        await ToggleFavoriteTrackAsync(changed[0]);
        var source = localSources[track.SourceInstanceId];
        var handler = new PlaylistFixtureHandler();
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, track.SourceInstanceId);
        playlistTestClient = api;
        try
        {
            ViewModel.PlaybackRouter.Unregister(source.Source.Id);
            ViewModel.PlaybackRouter.Register(new CapabilityPlaybackSource(source, source.Source.Capabilities | MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists));
            var remoteTrack = track with { AlbumId = "exact-album", Album = "同名专辑", Artists = [new("first-artist", "同名歌手"), new("second-artist", "同名歌手")] };
            var destinations = await api.ListPlaylistsAsync(default);
            RenderNasPlaylistNavigation(api, destinations);
            var remoteMenu = CreateTrackContextMenu(TrackList, remoteTrack, true);
            verify("context_remote_album_uses_explicit_reference", Item(remoteMenu, "album").IsEnabled);
            verify("context_multiple_artists_keep_distinct_ids", Sub(remoteMenu, "artists").Items.OfType<MenuFlyoutItem>().Select(i => (string)i.Tag).SequenceEqual(new[] { "first-artist", "second-artist" }));
            verify("context_remote_playlist_submenu_enabled_for_own_source", Sub(remoteMenu, "nas-playlists").IsEnabled && Sub(remoteMenu, "nas-playlists").Items.Count == 2);
            var destination = destinations.Single().Reference;
            var remoteSub = Sub(remoteMenu, "nas-playlists");
            await InvokeSubmenu(remoteMenu, remoteSub, remoteSub.Items.OfType<MenuFlyoutItem>().Single(i => Equals(i.Tag, destination)));
            verify("context_remote_add_readback_targets_hit_track", handler.Writes == 1 && handler.Tracks.Single() == remoteTrack.Id);
            await AddContextToNasPlaylistAsync(foreign, destination);
            verify("context_remote_add_rejects_foreign_source_before_transport", handler.Writes == 1);
            await AddContextToNasPlaylistAsync(remoteTrack, destination);
            verify("context_remote_add_deduplicates_without_write", handler.Writes == 1);
            var related = remoteTrack with { Id = "remote-related" };
            await AddContextToLocalPlaylistAsync(related, target.Id);
            await ShowLocalPlaylistsAsync(target.Id);
            var relatedView = activePlaylistPage ?? throw new InvalidOperationException();
            var relatedItem = relatedView.List.Items.Cast<object>().Select(ContextTrack).OfType<MusicTrack>().Single(t => t.Reference == related.Reference);
            var relatedMenu = CreateTrackContextMenu(relatedView.List, relatedItem, false);
            verify("context_local_snapshot_restores_remote_detail_references", Item(relatedMenu, "album").IsEnabled && Sub(relatedMenu, "artists").Items.Count == 2);
            await OpenContextCollectionAsync(remoteTrack, CollectionKind.Artist, "second-artist", "同名歌手");
            verify("context_artist_navigation_uses_requested_id", collectionKind == CollectionKind.Artist && collection?.Id == "second-artist" && LibraryPanel.Visibility == Visibility.Visible);
            await OpenContextCollectionAsync(remoteTrack, CollectionKind.Album, "exact-album", "同名专辑");
            verify("context_album_navigation_uses_requested_id", collectionKind == CollectionKind.Album && collection?.Id == "exact-album");
            ViewModel.PlaybackRouter.Unregister(source.Source.Id);
            ViewModel.PlaybackRouter.Register(new CapabilityPlaybackSource(source, source.Source.Capabilities | MusicSourceCapabilities.ReadPlaylists));
            verify("context_readonly_remote_playlist_add_disabled", !Sub(CreateTrackContextMenu(TrackList, remoteTrack, true), "nas-playlists").IsEnabled);
            await AddContextToNasPlaylistAsync(remoteTrack with { Id = "readonly" }, destination);
            verify("context_readonly_remote_add_no_write", handler.Writes == 1);
            playlistTestClient = null;
            await OpenContextCollectionAsync(remoteTrack, CollectionKind.Artist, "stale", "旧账号");
            verify("context_changed_account_cannot_navigate_old_reference", collection?.Id == "exact-album");
        }
        finally
        {
            playlistTestClient = null;
            ViewModel.PlaybackRouter.Unregister(source.Source.Id); ViewModel.PlaybackRouter.Register(source);
            ClearNasPlaylistNavigation();
        }
        Stop_Click(this, new RoutedEventArgs()); queue.Clear(); CloseTrackContextMenu();
    }
}
#endif
