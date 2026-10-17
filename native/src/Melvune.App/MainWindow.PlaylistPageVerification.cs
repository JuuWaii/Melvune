#if DEBUG
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private async Task VerifyPlaylistPagePresentationAsync(Action<string, bool> verify, PlaylistPageView view, string prefix)
    {
        verify(prefix + "_mounted_in_main_content", PlaylistPanel.Visibility == Visibility.Visible
            && ReferenceEquals(PlaylistPanel.Content, view.Layout) && SidebarHost.Visibility == Visibility.Visible
            && LibraryPanel.Visibility == Visibility.Collapsed && HomePanel.Visibility == Visibility.Collapsed
            && SettingsPanel.Visibility == Visibility.Collapsed && !themedDialogOpen);
        Root.UpdateLayout();
        if (prefix == "playlist_page")
            verify(prefix + "_loading_status_settled", !view.Status.Text.Contains("正在读取"));
        bool FooterFits()
        {
            var origin = view.Status.TransformToVisual(view.Layout).TransformPoint(new global::Windows.Foundation.Point());
            var action = view.Actions.TransformToVisual(view.Layout).TransformPoint(new global::Windows.Foundation.Point());
            return view.List.ActualHeight >= view.List.MinHeight && origin.Y >= 0
                && origin.Y + view.Status.ActualHeight <= view.Layout.ActualHeight + 1
                && action.Y >= 0 && action.Y + view.Actions.ActualHeight <= view.Layout.ActualHeight + 1;
        }
        verify(prefix + "_list_actions_and_status_outside_header_scroll", !view.Fields.Children.Contains(view.List)
            && !view.Fields.Children.Contains(view.Status) && Grid.GetRow(view.List) == 2 && Grid.GetRow(view.Status) == 4 && FooterFits());
        var items = view.List.ItemsSource;
        var selected = view.List.SelectedItem;
        var audio = music.Player.Source;
        var queueTrack = queue.Current?.Reference;
        view.List.Focus(FocusState.Programmatic);
        await OpenNowPlayingAsync();
        verify(prefix + "_lyrics_covers_playlist_without_disposing_page", PlaylistPanel.Visibility == Visibility.Collapsed
            && ReferenceEquals(activePlaylistPage, view) && !view.IsDisposed);
        await CloseNowPlayingAsync();
        verify(prefix + "_lyrics_returns_to_same_playlist_and_selection", PlaylistPanel.Visibility == Visibility.Visible
            && ReferenceEquals(view.List.ItemsSource, items) && ReferenceEquals(view.List.SelectedItem, selected)
            && ReferenceEquals(activePlaylistPage, view) && ReferenceEquals(music.Player.Source, audio) && queue.Current?.Reference == queueTrack);
        var size = AppWindow.Size;
        try
        {
            AppWindow.Resize(new global::Windows.Graphics.SizeInt32(820, 680));
            await Task.Delay(80); Root.UpdateLayout();
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, prefix + "-compact-layout.json"),
                System.Text.Json.JsonSerializer.Serialize(new { Fits = FooterFits(), HeaderHeight = view.Header.ActualHeight,
                    view.Header.MaxHeight, view.Header.ExtentHeight, view.Header.ViewportHeight, view.Header.ScrollableHeight }));
            verify(prefix + "_compact_footer_and_status_visible", FooterFits() && view.Header.ActualHeight <= view.Header.MaxHeight + 1
                && (view.Header.ExtentHeight <= view.Header.ViewportHeight + 1 || view.Header.ScrollableHeight > 0));
        }
        finally { AppWindow.Resize(size); await Task.Delay(80); Root.UpdateLayout(); }
    }
    private async Task VerifyPlaylistPageNavigationAsync(Action<string, bool> verify)
    {
        await ShowLocalMusicAsync();
        var oldItems = TrackList.ItemsSource;
        var oldSelection = TrackList.SelectedItem;
        int oldPage = page;
        string oldQuery = searchQuery;
        var stored = await playlistStore.LoadAsync();
        var first = new LocalPlaylist(Guid.NewGuid(), "同名歌单", []);
        var second = new LocalPlaylist(Guid.NewGuid(), "同名歌单", []);
        var offlineSource = Guid.NewGuid();
        var longPlaylist = new LocalPlaylist(Guid.NewGuid(), "长歌单夹具", Enumerable.Range(1, 200).Select(i =>
            LocalPlaylistEntry.FromTrack(new MusicTrack("offline-" + i, "长列表歌曲 " + i, "夹具", 30, false) { SourceInstanceId = offlineSource })).ToArray());
        var latest = await playlistStore.SaveAsync(stored with { Playlists = [first, second, longPlaylist] });
        await RefreshPlaylistNavigationAsync();
        verify("playlist_sidebar_lists_same_name_by_distinct_identity", LocalPlaylistNavigation.Items.Count == 3
            && ((LocalPlaylist)LocalPlaylistNavigation.Items[0]).Id == first.Id && ((LocalPlaylist)LocalPlaylistNavigation.Items[1]).Id == second.Id);
        await ShowLocalPlaylistsAsync(second.Id);
        verify("playlist_sidebar_entry_opens_requested_identity", activePlaylistPage?.LocalPlaylistId == second.Id
            && LocalPlaylistNavigation.SelectedItem is LocalPlaylist nav && nav.Id == second.Id);
        var oldView = activePlaylistPage ?? throw new InvalidOperationException();
        await ShowLocalPlaylistsAsync(longPlaylist.Id);
        var view = activePlaylistPage ?? throw new InvalidOperationException();
        verify("playlist_navigation_disposes_previous_page", oldView.IsDisposed && !ReferenceEquals(oldView, view) && view.List.Items.Count == 200);
        await VerifyPlaylistPagePresentationAsync(verify, view, "playlist_long_page");
        view.List.ScrollIntoView(view.List.Items[^1]);
        await Task.Delay(100);
        verify("playlist_long_page_scrolls_independently", FindPresentationScroll(view.List)?.VerticalOffset > 0 && view.Status.IsLoaded && view.PlayButton.IsLoaded);
        view.BlockNavigation = true;
        try
        {
            await ShowHomeAsync();
            verify("playlist_mutation_blocks_navigation", ReferenceEquals(activePlaylistPage, view)
                && PlaylistPanel.Visibility == Visibility.Visible && HomePanel.Visibility == Visibility.Collapsed && view.Status.Text.Contains("保存"));
        }
        finally { view.BlockNavigation = false; }
        await ReturnFromPlaylistPageAsync();
        verify("playlist_back_preserves_browse_context", LibraryPanel.Visibility == Visibility.Visible && PlaylistPanel.Visibility == Visibility.Collapsed
            && ReferenceEquals(oldItems, TrackList.ItemsSource) && ReferenceEquals(oldSelection, TrackList.SelectedItem) && page == oldPage && searchQuery == oldQuery);
        await VerifyPlaylistCreationDialogAsync(verify, () => ShowLocalPlaylistsAsync(createNew: true), null, "playlist_plus_cancel", 120);
        verify("playlist_plus_cancel_preserves_catalog_and_page", activePlaylistPage is null && LibraryPanel.Visibility == Visibility.Visible
            && (await playlistStore.LoadAsync()).Revision == latest.Revision);
        await VerifyPlaylistCreationDialogAsync(verify, () => ShowLocalPlaylistsAsync(createNew: true), "  加号新歌单  ", "playlist_plus_create", 120);
        var created = (await playlistStore.LoadAsync()).Playlists.Last();
        verify("playlist_plus_create_trims_and_selects_sidebar", created.Name == "加号新歌单" && created.Entries.Count == 0
            && LocalPlaylistNavigation.SelectedItem is LocalPlaylist selected && selected.Id == created.Id
            && activePlaylistPage?.List.Items.Count == 0 && (await playlistStore.LoadAsync()).Playlists.Count == 4);
        verify("sidebar_unified_playlists_and_management_entries", PlaylistsNavigation.IsLoaded && MusicManagementNavigation.IsLoaded
            && UnifiedPlaylistNavigation.IsLoaded && FindSongVisual<Button>(SidebarHost, button => Equals(button.Content, "设置")) is null);
        await ShowHomeAsync();
        verify("home_navigation_is_independent_of_remote_connection", HomePanel.Visibility == Visibility.Visible && PlaylistPanel.Visibility == Visibility.Collapsed && activePlaylistPage is null);
        await ShowSettingsPanelAsync();
    }
}
#endif
