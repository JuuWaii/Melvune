#if DEBUG
using System.Net;
using System.Text;
using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private sealed class PlaylistFixtureHandler : HttpMessageHandler
    {
        public string Name = "飞牛夹具";
        public bool Exists = true;
        public readonly List<string> Tracks = [];
        public int Writes;
        public Task? PauseListResponse;
        public TaskCompletionSource? ListResponseEntered;
        public Task? PauseDetailResponse;
        public TaskCompletionSource? DetailResponseEntered;
        private long version = 1;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object Summary() => new { guid = "fixture", name = Name, coverId = (string?)null, updatedAt = version };
            string path = request.RequestUri!.AbsolutePath; object? data = null;
            if (request.Method == HttpMethod.Get && path.EndsWith("/playlist/list") && PauseListResponse is { } pause)
            { ListResponseEntered?.TrySetResult(); await pause; }
            if (request.Method == HttpMethod.Get && path.EndsWith("/playlist/detail") && PauseDetailResponse is { } detailPause)
            { DetailResponseEntered?.TrySetResult(); await detailPause; }
            if (request.Method == HttpMethod.Post)
            {
                Writes++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = body.RootElement;
                if (path.EndsWith("/create")) { Name = root.GetProperty("name").GetString()!; Exists = true; Tracks.Clear(); data = Summary(); }
                if (path.EndsWith("/edit")) Name = root.GetProperty("name").GetString()!;
                if (path.EndsWith("/delete")) Exists = false;
                if (path.EndsWith("/add-track")) Tracks.AddRange(root.GetProperty("trackGUIDs").EnumerateArray().Select(value => value.GetString()!));
                if (path.EndsWith("/remove-track")) Tracks.RemoveAll(id => root.GetProperty("trackGUIDs").EnumerateArray().Any(value => value.GetString() == id));
                version++;
            }
            else if (path.EndsWith("/detail")) data = Summary();
            else if (path.Contains("/track/playlist-detail/")) data = new { total = Tracks.Count, list = Tracks.Select(id => new { guid = id, title = "飞牛夹具歌曲", duration = 1000 }) };
            else data = new { list = Exists ? new[] { Summary() } : [] };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = 0, data }), Encoding.UTF8, "application/json") };
        }
    }
    private async Task VerifyNasPlaylistWindowAsync(Action<string, bool> verify)
    {
        var handler = new PlaylistFixtureHandler();
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, Guid.NewGuid());
        playlistTestClient = api;
        ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
        TrackList.ItemsSource = new[] { new MusicTrack("fixture-track", "飞牛夹具歌曲", "夹具", 1, false) { SourceInstanceId = api.SourceInstanceId } };
        TrackList.SelectedIndex = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        nasPlaylistPageOpened = async view =>
        {
            try
            {
                var editor = view.Editor; var panel = view.Fields;
                await VerifyPlaylistPagePresentationAsync(verify, view, "nas_playlist_page");
                var list = view.List;
                verify("nas_playlist_detail_has_no_catalog_form", !panel.Children.OfType<TextBox>().Any() && !panel.Children.OfType<ComboBox>().Any() && !panel.Children.OfType<CheckBox>().Any());
                var status = view.Status;
                var buttons = panel.Children.OfType<Button>().Concat(panel.Children.OfType<StackPanel>().SelectMany(item => item.Children.OfType<Button>())).Concat(view.Actions.Children.OfType<Button>()).ToArray();
                async Task Click(string label)
                {
                    var button = buttons.Single(item => item.Content.ToString() == label);
                    if (!button.IsEnabled) throw new InvalidOperationException();
                    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Task.Delay(100);
                    for (int i = 0; i < 100 && !editor.IsEnabled; i++) await Task.Delay(50);
                    if (!editor.IsEnabled) throw new TimeoutException();
                }
                verify("nas_playlist_empty_play_disabled", !view.PlayButton.IsEnabled && list.Items.Count == 0);
                await VerifyPlaylistCreationDialogAsync(verify, async () => { await CreateNasPlaylistFromDialogAsync(api); }, "新建飞牛夹具", "nas_sidebar_new", 32);
                verify("nas_playlist_ui_create", handler.Name == "新建飞牛夹具" && status.Text.Contains("核对"));
                await Click("加入列表中所选飞牛歌曲");
                verify("nas_playlist_ui_add", list.Items.Count == 1 && view.PlayButton.IsEnabled);
                await VerifySharedSongRowAsync(verify, list, "nas_playlist", artwork: false);
                var expected = NasPlaylistNavigation.Items.Cast<NasPlaylist>().Single();
                await VerifyPlaylistCreationDialogAsync(verify, () => RenameNasPlaylistAsync(api, expected), "客户端改名", "nas_sidebar_rename", 32, primaryLabel: "保存");
                verify("nas_playlist_ui_rename", handler.Name == "客户端改名");
                verify("nas_playlist_sidebar_rename_updated", NasPlaylistNavigation.Items.Cast<NasPlaylist>().Single().Name == "客户端改名");
                int writes = handler.Writes;
                handler.Name = "网页改名";
                expected = NasPlaylistNavigation.Items.Cast<NasPlaylist>().Single();
                bool conflict = false;
                try { await VerifyPlaylistCreationDialogAsync(verify, () => RenameNasPlaylistAsync(api, expected), "过期修改", "nas_sidebar_stale_rename", 32, primaryLabel: "保存"); }
                catch (RemotePlaylistConflictException) { conflict = true; }
                verify("nas_playlist_ui_conflict", conflict && handler.Writes == writes);
                await Click("刷新");
                verify("nas_playlist_ui_refresh", view.TitleText.Text == "网页改名" && list.Items.Count == 1);
                ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
                ViewModel.PlaybackRouter.Register(new CapabilityPlaybackSource(new NasPlaybackSource(api), MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.Playback));
                writes = handler.Writes;
                await Click("刷新");
                verify("nas_playlist_read_only_controls", list.Items.Count == 1 && view.PlayButton.IsEnabled
                    && buttons.Where(button => button.Content.ToString() is not ("刷新" or "播放所选")).All(button => !button.IsEnabled)
                    && CreatePlaylistNavigationMenu(NasPlaylistNavigation.Items[0]).Items.OfType<MenuFlyoutItem>().Where(item => !Equals(item.Tag, "open")).All(item => !item.IsEnabled));
                verify("nas_playlist_read_only_refresh_no_write", handler.Writes == writes && status.Text.Contains("核对"));
                ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
                ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
                await Click("刷新");
                verify("nas_playlist_write_capability_restored", buttons.Single(button => button.Content.ToString() == "移除所选歌曲").IsEnabled
                    && CreatePlaylistNavigationMenu(NasPlaylistNavigation.Items[0]).Items.OfType<MenuFlyoutItem>().All(item => item.IsEnabled));
                await Click("移除所选歌曲");
                verify("nas_playlist_ui_remove", list.Items.Count == 0 && handler.Tracks.Count == 0);
                await VerifyPlaylistDeleteDialogAsync(() => DeleteNasPlaylistAsync(api, NasPlaylistNavigation.Items.Cast<NasPlaylist>().Single()), accept: false);
                verify("nas_playlist_sidebar_cancel_delete_keeps_server", handler.Exists);
                var deleteExpected = NasPlaylistNavigation.Items.Cast<NasPlaylist>().Single();
                bool deleteConflict = false;
                try { await VerifyPlaylistDeleteDialogAsync(() => DeleteNasPlaylistAsync(api, deleteExpected), true, beforeAccept: () => handler.Tracks.Add("网页新增")); }
                catch (RemotePlaylistConflictException) { deleteConflict = true; }
                verify("nas_playlist_sidebar_delete_preserves_concurrent_tracks", deleteConflict && handler.Exists && handler.Tracks.Contains("网页新增"));
                await Click("刷新"); await Click("移除所选歌曲");
                await VerifyPlaylistDeleteDialogAsync(() => DeleteNasPlaylistAsync(api, NasPlaylistNavigation.Items.Cast<NasPlaylist>().Single()), accept: true);
                verify("nas_playlist_ui_delete", !handler.Exists && list.Items.Count == 0);
                verify("nas_playlist_sidebar_delete_updated", NasPlaylistNavigation.Items.Count == 0);
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (ReferenceEquals(activePlaylistPage, view)) await ReturnFromPlaylistPageAsync(); }
            done.TrySetResult();
        };
        try
        {
            await ShowNasPlaylistsAsync(api); await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            nasPlaylistPageOpened = null;
            int createWrites = handler.Writes;
            await VerifyPlaylistCreationDialogAsync(verify, () => ShowNasPlaylistsAsync(api, createNew: true), null, "nas_plus_cancel", 32);
            verify("nas_plus_cancel_never_writes", handler.Writes == createWrites && !handler.Exists);
            await VerifyPlaylistCreationDialogAsync(verify, () => ShowNasPlaylistsAsync(api, createNew: true), "  加号飞牛歌单  ", "nas_plus_create", 32);
            verify("nas_plus_create_confirms_and_selects_sidebar", handler.Name == "加号飞牛歌单" && handler.Writes == createWrites + 1
                && NasPlaylistNavigation.SelectedItem is NasPlaylist selected && selected.Name == "加号飞牛歌单"
                && activePlaylistPage?.List.Items.Count == 0);
            await ReturnFromPlaylistPageAsync();
            ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
            ViewModel.PlaybackRouter.Register(new CapabilityPlaybackSource(new NasPlaybackSource(api), MusicSourceCapabilities.ReadPlaylists));
            createWrites = handler.Writes;
            verify("nas_plus_read_only_never_opens_or_writes", await CreateNasPlaylistFromDialogAsync(api) is null && !themedDialogOpen && handler.Writes == createWrites);
            ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
            ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
            await VerifyPlaylistCreationDialogAsync(verify, async () => { await CreateNasPlaylistFromDialogAsync(api); }, "旧会话禁止创建", "nas_plus_session_change", 32,
                beforeAccept: () => playlistTestClient = null);
            verify("nas_plus_session_change_before_accept_never_writes", handler.Writes == createWrites);
            playlistTestClient = api;
            var deleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deleteRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.DetailResponseEntered = deleteEntered; handler.PauseDetailResponse = deleteRelease.Task;
            var pendingDelete = DeleteNasPlaylistAsync(api, NasPlaylistNavigation.Items.Cast<NasPlaylist>().Single());
            try
            {
                await deleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await ShowHomeAsync();
                deleteRelease.TrySetResult(); await pendingDelete.WaitAsync(TimeSpan.FromSeconds(5));
                verify("nas_playlist_sidebar_late_delete_read_cannot_open_dialog_or_write", !themedDialogOpen && handler.Writes == createWrites
                    && HomePanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible);
            }
            finally { deleteRelease.TrySetResult(); await pendingDelete.WaitAsync(TimeSpan.FromSeconds(5)); handler.PauseDetailResponse = null; }
            handler.Exists = true; handler.Name = "旧账号歌单夹具";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.ListResponseEntered = entered; handler.PauseListResponse = release.Task;
            var pending = ShowNasPlaylistsAsync(api);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var heldView = activePlaylistPage ?? throw new InvalidOperationException();
                verify("nas_playlist_read_can_return_without_waiting_for_response", !heldView.Editor.IsEnabled && heldView.BackButton.IsEnabled && !heldView.BlockNavigation);
                heldView.BlockNavigation = true;
                ResetLibrary();
                playlistTestClient = null; ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
                verify("nas_playlist_session_change_closes_even_busy_page", heldView.IsDisposed && activePlaylistPage is null
                    && PlaylistPanel.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed && HomePanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                    && NasPlaylistNavigation.Items.Count == 0);
                release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
                verify("nas_playlist_late_response_cannot_restore_previous_account_page", activePlaylistPage is null
                    && NasPlaylistNavigation.Items.Count == 0 && HomePanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible);
            }
            finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally { ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId); playlistTestClient = null; nasPlaylistPageOpened = null; }
    }
}
#endif
