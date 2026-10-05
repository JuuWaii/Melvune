#if DEBUG
using System.Net;
using System.Text;
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private sealed class AllMusicFixtureHandler(string collisionId) : HttpMessageHandler
    {
        public int Calls, TrackCount = 55;
        public bool Fail;
        public Task? Pause;
        public TaskCompletionSource? Entered;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Pause is { } wait) { Entered?.TrySetResult(); await wait; }
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
            int index = int.Parse(query["page"]), size = int.Parse(query["size"]);
            var tracks = Enumerable.Range(1, TrackCount).Select(i => new { guid = i == 1 ? collisionId : "remote-" + i, title = "same 远程音乐 " + i, duration = 60000 });
            if (query.TryGetValue("q", out var term)) tracks = tracks.Where(track => track.title.Contains(term, StringComparison.OrdinalIgnoreCase));
            var result = tracks.ToArray();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = 0,
                data = new { total = result.Length, list = result.Skip((index - 1) * size).Take(size) } }), Encoding.UTF8, "application/json") };
        }
    }

    private async Task WaitAllMusicViewAsync()
    {
        for (int i = 0; i < 100 && PageStatus.Text.StartsWith("正在"); i++) await Task.Delay(50);
        if (PageStatus.Text.StartsWith("正在")) throw new TimeoutException();
        Root.UpdateLayout();
    }

    private async Task VerifyAllMusicWindowAsync(Action<string, bool> verify)
    {
        var catalog = await localLibraryStore.LoadAsync(); ApplyLocalCatalog(catalog); await RestoreLocalSourcesAsync(default);
        var localTracks = localSources.Values.SelectMany(source => source.Tracks).ToArray();
        var handler = new AllMusicFixtureHandler(localTracks[0].Id);
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, Guid.NewGuid());
        allMusicTestClient = api; remoteCapabilityTestSource = api.SourceInstanceId;
        ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(api.SourceInstanceId, MusicSourceCapabilities.Browse | MusicSourceCapabilities.Search));
        try
        {
            await ShowAllMusicAsync(); Root.UpdateLayout();
            MusicTrack[] Rows() => ((IEnumerable<MusicTrack>)TrackList.ItemsSource).ToArray();
            verify("all_music_single_entry_and_source_filter", allMusicView && AllMusicNavigation.IsEnabled && BrowseSourceFilter.Visibility == Visibility.Visible
                && BrowseSourceFilter.SelectedIndex == 0 && FindSongVisual<TextBlock>(SidebarHost, text => text.Text == "本地音乐") is null);
            var size = AppWindow.Size;
            try
            {
                AppWindow.Resize(new global::Windows.Graphics.SizeInt32(820, 560)); await Task.Delay(120); Root.UpdateLayout();
                var origin = TrackList.TransformToVisual(LibraryPanel).TransformPoint(new global::Windows.Foundation.Point());
                verify("all_music_compact_header_keeps_song_list_visible", TrackList.ActualHeight >= 79 && origin.Y + TrackList.ActualHeight <= LibraryPanel.ActualHeight + 1
                    && LibraryHeaderScroll.ActualHeight <= LibraryHeaderScroll.MaxHeight + 1 && LibraryHeaderScroll.ScrollableHeight > 0
                    && BrowseSourceFilter.ActualWidth > 100 && LocalGroupFilter.ActualWidth > 100 && LocalLibraryFilter.ActualWidth > 100);
            }
            finally { AppWindow.Resize(size); await Task.Delay(100); Root.UpdateLayout(); }
            verify("all_music_remote_first_page_bounded", total == 55 + localTracks.Length && page == 1 && Rows().Length == 50
                && Rows().All(track => track.SourceInstanceId == api.SourceInstanceId) && handler.Calls == 1 && NextPage.IsEnabled);
            var remoteCollision = Rows()[0];
            await LoadPageAsync(2);
            verify("all_music_cross_source_boundary_page", page == 2 && Rows().Length == 5 + localTracks.Length
                && Rows().Take(5).All(track => track.SourceInstanceId == api.SourceInstanceId)
                && Rows().Skip(5).All(track => localSources.ContainsKey(track.SourceInstanceId)) && !NextPage.IsEnabled && handler.Calls == 3);
            handler.TrackCount = 49; await LoadPageAsync(1);
            verify("all_music_partial_remote_page_fills_with_local", Rows().Length == 50 && Rows()[49].Reference == localTracks.OrderBy(track => track.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(track => track.SourceInstanceId).ThenBy(track => track.Id).First().Reference && total == 49 + localTracks.Length);
            await LoadPageAsync(2);
            verify("all_music_local_tail_has_no_duplicate_boundary", Rows().Length == localTracks.Length - 1 && Rows().All(track => localSources.ContainsKey(track.SourceInstanceId)));
            handler.TrackCount = 55;
            BrowseSourceFilter.SelectedIndex = 2; await WaitAllMusicViewAsync();
            int before = handler.Calls;
            verify("all_music_local_scope_has_no_remote_requests", total == localTracks.Length && Rows().All(track => localSources.ContainsKey(track.SourceInstanceId))
                && LocalLibraryFilters.Visibility == Visibility.Visible);
            var first = Rows()[0]; TrackList.SelectedItem = first; await StartQueueAsync(first);
            for (int i = 0; i < 100 && !mediaReady; i++) await Task.Delay(50);
            music.Player.Pause(); var audio = music.Player.Source;
            verify("all_music_local_scope_plays_file", mediaReady && queue.Current?.Reference == first.Reference && audio is not null && handler.Calls == before);
            LocalGroupFilter.SelectedIndex = 1; await WaitAllMusicViewAsync();
            verify("all_music_local_group_filter", total == 1 && selectedLocalGroup is not null && Rows().All(track => catalog.Libraries.Single(item => item.SourceId == track.SourceInstanceId).Group == selectedLocalGroup));
            LocalGroupFilter.SelectedIndex = 0; await WaitAllMusicViewAsync();
            BrowseSourceFilter.SelectedIndex = 0; await WaitAllMusicViewAsync();
            SearchInput.Text = "远程音乐"; await SearchAsync();
            verify("all_music_search_keeps_unified_scope", allMusicView && total == 55 && Rows().All(track => track.SourceInstanceId == api.SourceInstanceId));
            SearchInput.Text = "same"; await SearchAsync(); await LoadPageAsync(2);
            verify("all_music_search_combines_both_sources", total == 55 + localTracks.Length && Rows().Any(track => localSources.ContainsKey(track.SourceInstanceId)));
            var oldItems = TrackList.ItemsSource;
            var oldSources = localSources.ToDictionary(item => item.Key, item => item.Value);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int scanned = 0;
            localPageMetadataReader = async (_, ct) => { if (++scanned == 2) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return new("取消扫描", "夹具", 60); };
            try
            {
                var pending = LoadAllMusicPageAsync(1, true); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                CancelLocalScan_Click(this, new RoutedEventArgs()); await pending;
                verify("all_music_rescan_cancel_keeps_sources_page_and_audio", oldSources.All(item => ReferenceEquals(localSources[item.Key], item.Value))
                    && ReferenceEquals(oldItems, TrackList.ItemsSource) && page == 2 && total == 55 + localTracks.Length && ReferenceEquals(audio, music.Player.Source));
            }
            finally { localPageMetadataReader = null; }
            handler.Fail = true; await LoadPageAsync(1);
            verify("all_music_remote_failure_keeps_local_and_explicit_status", total == localTracks.Length && Rows().All(track => localSources.ContainsKey(track.SourceInstanceId))
                && PageStatus.Text.Contains("飞牛读取失败") && ReferenceEquals(audio, music.Player.Source));
            handler.Fail = false; await LoadPageAsync(1);
            verify("all_music_remote_retry_restores_total", total == 55 + localTracks.Length && NextPage.IsEnabled);
            ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId); before = handler.Calls;
            await LoadPageAsync(1);
            verify("all_music_disconnected_source_skipped_without_transport", total == localTracks.Length && handler.Calls == before && AllMusicNavigation.IsEnabled && SearchAction.IsEnabled);
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(api.SourceInstanceId, MusicSourceCapabilities.Browse | MusicSourceCapabilities.Search));
            await LoadPageAsync(2); var mixed = Rows();
            verify("all_music_same_provider_id_remains_distinct_reference", mixed.Any(track => track.Id == remoteCollision.Id && track.Reference != remoteCollision.Reference)
                && remoteCollision.SourceInstanceId == api.SourceInstanceId && localTracks.Any(track => track.Reference == first.Reference));
            ResetLibrary();
            verify("all_music_reset_keeps_only_local_rows_and_playback", allMusicView && Rows().All(track => localSources.ContainsKey(track.SourceInstanceId))
                && ReferenceEquals(audio, music.Player.Source) && PageStatus.Text.Contains("刷新") && !NextPage.IsEnabled);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); handler.Pause = release.Task; handler.Entered = entered;
            var late = LoadPageAsync(1); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ShowLocalMusicAsync(); var localItems = TrackList.ItemsSource;
            release.TrySetResult(); await late; handler.Pause = null;
            verify("all_music_late_remote_read_cannot_replace_next_view", !allMusicView && localView && ReferenceEquals(localItems, TrackList.ItemsSource) && ReferenceEquals(audio, music.Player.Source));
        }
        finally
        {
            allMusicTestClient = null; remoteCapabilityTestSource = null; ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
            localPageMetadataReader = null; searchQuery = SearchInput.Text = ""; allMusicView = false; UpdateCollectionView();
        }
    }
}
#endif
