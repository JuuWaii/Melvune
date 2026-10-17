#if DEBUG
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private static void InvokePlaylistManagerCard(PlaylistPageView view, PlaylistManagerRow row, string action)
    {
        view.List.ScrollIntoView(row); view.List.UpdateLayout();
        var container = view.List.ContainerFromItem(row) as ListViewItem ?? throw new InvalidOperationException();
        var button = FindSongVisual<Button>(container, item => Equals(item.Tag, action)) ?? throw new InvalidOperationException();
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private async Task WaitPlaylistManagerAsync(string title)
    {
        for (int i = 0; i < 100 && (activePlaylistPage?.TitleText.Text != title || !activePlaylistPage.Editor.IsEnabled); i++) await Task.Delay(50);
        if (activePlaylistPage?.TitleText.Text != title || !activePlaylistPage.Editor.IsEnabled) throw new TimeoutException();
    }
    private async Task VerifyStandalonePlaylistManagersAsync()
    {
        var checks = new Dictionary<string, bool>();
        string stage = "setup", error = "", diagnostic = "";
        void Verify(string key, bool value)
        { stage = key; checks[key] = value; if (!value) throw new InvalidOperationException(key); }
        try { await VerifyPlaylistManagersAsync(Verify); stage = "complete"; }
        catch (Exception ex) { error = ex.GetType().Name; diagnostic = ex.ToString(); }
        await File.WriteAllTextAsync(Path.Combine(ThemeSettingsDirectory(), "playlist-managers-verification.json"),
            System.Text.Json.JsonSerializer.Serialize(new { stage, error, checks, diagnostic }));
    }
    private async Task VerifyPlaylistManagersAsync(Action<string, bool> verify)
    {
        CloseFullPage(force: true); ClosePlaylistPage(); await ShowHomeAsync();
        var original = await playlistStore.LoadAsync();
        var first = new LocalPlaylist(Guid.NewGuid(), "管理歌单一", []);
        var second = new LocalPlaylist(Guid.NewGuid(), "管理歌单二", []);
        await playlistStore.SaveAsync(original with { Playlists = [first, second] });
        var audio = music.Player.Source; var queueTrack = queue.Current?.Reference;
        PlaylistPageView View() => activePlaylistPage ?? throw new InvalidOperationException();
        playlistManagerOpened = view => verify("playlist_manager_loaded_hook_" + view.TitleText.Text, ReferenceEquals(activePlaylistPage, view) && !view.IsDisposed);
        try
        {
            await ShowPlaylistManagerAsync(false);
            await WaitPlaylistManagerAsync("本地歌单管理");
            var view = View();
            verify("local_playlist_category_opens_manager_without_implicit_detail", view.List.Items.Count == 2 && view.LocalPlaylistId is null
                && ReferenceEquals(view.List.ItemTemplate, Root.Resources["PlaylistManagerCardTemplate"]) && view.PlayButton.Visibility == Visibility.Collapsed);
            await VerifyPlaylistPagePresentationAsync(verify, view, "local_playlist_manager");
            Root.UpdateLayout();
            var row = view.List.Items.Cast<PlaylistManagerRow>().Single(item => ((LocalPlaylist)item.Value).Id == second.Id);
            var container = view.List.ContainerFromItem(row) as ListViewItem ?? throw new InvalidOperationException();
            verify("playlist_manager_card_actions_are_rendered_and_named", new[] { "open", "rename", "delete" }.All(action =>
                FindSongVisual<Button>(container, button => Equals(button.Tag, action)) is { IsLoaded: true, ActualWidth: > 0 } button && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
            var search = view.Fields.Children.OfType<TextBox>().Single();
            search.Text = "歌单二"; await Task.Delay(60);
            verify("playlist_manager_filter_keeps_stable_identity", view.List.Items.Count == 1 && ((LocalPlaylist)((PlaylistManagerRow)view.List.Items[0]).Value).Id == second.Id);
            search.Text = "没有匹配"; await Task.Delay(60);
            verify("playlist_manager_no_match_has_explicit_state", view.List.Items.Count == 0 && view.Status.Text.Contains("没有匹配"));
            search.Text = ""; await Task.Delay(60);
            row = view.List.Items.Cast<PlaylistManagerRow>().Single(item => ((LocalPlaylist)item.Value).Id == second.Id);
            InvokePlaylistManagerCard(view, row, "open");
            for (int i = 0; i < 100 && activePlaylistPage?.LocalPlaylistId != second.Id; i++) await Task.Delay(50);
            verify("playlist_manager_open_targets_selected_card", View().LocalPlaylistId == second.Id && View().TitleText.Text == second.Name && view.IsDisposed);
            await ReturnFromPlaylistPageAsync(); await WaitPlaylistManagerAsync("本地歌单管理"); view = View();
            verify("playlist_detail_returns_to_parent_manager_and_selection", view.List.SelectedItem is PlaylistManagerRow { Value: LocalPlaylist selected } && selected.Id == second.Id
                && ReferenceEquals(audio, music.Player.Source) && queue.Current?.Reference == queueTrack);
            row = (PlaylistManagerRow)view.List.SelectedItem;
            await VerifyPlaylistCreationDialogAsync(verify, () => playlistManagerAction!(row, "rename"), "管理页改名", "manager_local_rename", 120, primaryLabel: "保存");
            verify("playlist_manager_rename_refreshes_cards_and_sidebar", view.List.Items.Cast<PlaylistManagerRow>().Any(item => item.Name == "管理页改名")
                && (LocalPlaylistNavigation.ItemsSource as IEnumerable<LocalPlaylist>)!.Any(item => item.Id == second.Id && item.Name == "管理页改名"));
            row = view.List.Items.Cast<PlaylistManagerRow>().Single(item => ((LocalPlaylist)item.Value).Id == second.Id);
            await VerifyPlaylistDeleteDialogAsync(() => playlistManagerAction!(row, "delete"), false);
            verify("playlist_manager_delete_cancel_preserves_local", (await playlistStore.LoadAsync()).Playlists.Count == 2);
            verify("playlist_manager_cancel_restores_summary_without_unrelated_playback_status", view.Status.Text == "2 个歌单 · 当前显示 2 个。");
            await VerifyPlaylistDeleteDialogAsync(() => playlistManagerAction!(row, "delete"), true);
            verify("playlist_manager_delete_refreshes_only_selected_card", view.List.Items.Count == 1 && ((LocalPlaylist)((PlaylistManagerRow)view.List.Items[0]).Value).Id == first.Id);
            await VerifyPlaylistCreationDialogAsync(verify, () => playlistManagerAction!(null, "create"), "管理页新歌单", "manager_local_create", 120);
            verify("playlist_manager_create_stays_on_manager_and_selects_new", ReferenceEquals(View(), view)
                && view.List.SelectedItem is PlaylistManagerRow { Name: "管理页新歌单" } && view.List.Items.Count == 2);
            view.BlockNavigation = true; await ReturnFromPlaylistPageAsync();
            verify("playlist_manager_write_blocks_parent_return", ReferenceEquals(View(), view) && !view.IsDisposed);
            view.BlockNavigation = false;
            await ReturnFromPlaylistPageAsync();
            verify("playlist_manager_returns_to_original_home_and_keeps_audio", HomePanel.Visibility == Visibility.Visible && activePlaylistPage is null
                && ReferenceEquals(audio, music.Player.Source) && queue.Current?.Reference == queueTrack);
            await VerifyNasPlaylistManagerAsync(verify);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(System.Text.Json.JsonSerializer.Serialize(new
            {
                page = activePlaylistPage?.TitleText.Text,
                status = activePlaylistPage?.Status.Text,
                cards = activePlaylistPage?.List.Items.Cast<PlaylistManagerRow>().Select(item => item.Name).ToArray(),
                sidebar = (LocalPlaylistNavigation.ItemsSource as IEnumerable<LocalPlaylist>)?.Select(item => item.Name).ToArray(),
                saved = (await playlistStore.LoadAsync()).Playlists.Select(item => item.Name).ToArray(),
                queueOpen = queuePage is not null,
                fullPageOpen = fullPageView is not null,
                contextPlaylistWriteBusy,
                playbackStatus = PlaybackStatus.Text
            }), ex);
        }
        finally
        {
            playlistManagerOpened = null; ClosePlaylistPage();
            var latest = await playlistStore.LoadAsync(); await playlistStore.SaveAsync(latest with { Playlists = original.Playlists });
            await RefreshPlaylistNavigationAsync(); await ShowHomeAsync();
        }
    }
    private async Task VerifyNasPlaylistManagerAsync(Action<string, bool> verify)
    {
        var handler = new PlaylistFixtureHandler();
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, Guid.NewGuid());
        playlistTestClient = api;
        ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
        try
        {
            await ShowPlaylistManagerAsync(true, api);
            var view = activePlaylistPage ?? throw new InvalidOperationException();
            var row = view.List.Items.Cast<PlaylistManagerRow>().Single();
            verify("nas_playlist_manager_is_account_scoped_without_fake_count", view.RemoteSource == api.SourceInstanceId && row.CanOpen && row.CanWrite
                && row.Description.Contains("当前飞牛账号") && !row.Description.Contains("首"));
            await VerifyPlaylistPagePresentationAsync(verify, view, "nas_playlist_manager");
            InvokePlaylistManagerCard(view, row, "open");
            for (int i = 0; i < 100 && activePlaylistPage?.RemotePlaylistReference is null; i++) await Task.Delay(50);
            verify("nas_playlist_manager_open_has_exact_source_reference", activePlaylistPage?.RemotePlaylistReference == ((NasPlaylist)row.Value).Reference && view.IsDisposed);
            await ReturnFromPlaylistPageAsync(); await WaitPlaylistManagerAsync("飞牛歌单管理"); view = activePlaylistPage!;
            verify("nas_playlist_detail_returns_to_scoped_manager", view.RemoteSource == api.SourceInstanceId && view.List.SelectedItem is PlaylistManagerRow selected && selected.Key == row.Key);
            row = view.List.Items.Cast<PlaylistManagerRow>().Single();
            await VerifyPlaylistCreationDialogAsync(verify, () => playlistManagerAction!(row, "rename"), "飞牛管理页改名", "manager_nas_rename", 32, primaryLabel: "保存");
            verify("nas_playlist_manager_rename_is_read_back", handler.Name == "飞牛管理页改名" && view.List.Items.Cast<PlaylistManagerRow>().Single().Name == handler.Name);
            // Same source registration now advertises read-only access; the management page must follow it.
            ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(api.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists));
            UpdateSourceCommands();
            row = view.List.Items.Cast<PlaylistManagerRow>().Single();
            int writes = handler.Writes;
            verify("nas_playlist_manager_read_only_keeps_open_but_disables_writes", row.CanOpen && !row.CanWrite
                && !view.HeadingActions!.Children.OfType<Button>().Single().IsEnabled);
            await playlistManagerAction!(row, "delete"); await playlistManagerAction!(null, "create");
            verify("nas_playlist_manager_read_only_has_no_mutation", handler.Writes == writes && !themedDialogOpen);
            ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId); ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
            UpdateSourceCommands();
            row = view.List.Items.Cast<PlaylistManagerRow>().Single();
            await VerifyPlaylistDeleteDialogAsync(() => playlistManagerAction!(row, "delete"), true);
            verify("nas_playlist_manager_delete_refreshes_empty_state", !handler.Exists && view.List.Items.Count == 0 && view.Status.Text.Contains("删除"));
            await VerifyPlaylistCreationDialogAsync(verify, () => playlistManagerAction!(null, "create"), "飞牛管理页新歌单", "manager_nas_create", 32);
            verify("nas_playlist_manager_create_stays_and_selects_server_result", handler.Exists && handler.Name == "飞牛管理页新歌单"
                && ReferenceEquals(activePlaylistPage, view) && view.List.SelectedItem is PlaylistManagerRow selectedNew && selectedNew.Name == handler.Name);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.ListResponseEntered = entered; handler.PauseListResponse = release.Task;
            var refresh = view.Reload!(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ShowHomeAsync(); var homeContent = HomePanel.Visibility;
            release.TrySetResult(); await refresh;
            verify("nas_playlist_manager_late_list_cannot_restore_closed_page", view.IsDisposed && activePlaylistPage is null && homeContent == Visibility.Visible
                && HomePanel.Visibility == Visibility.Visible);
        }
        finally { ClosePlaylistPage(); ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId); playlistTestClient = null; }
    }
}
#endif
