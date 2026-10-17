#if DEBUG
using System.Net;
using System.Text;
using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private sealed class MetadataFixtureHandler : HttpMessageHandler
    {
        internal int Count = 2, Calls, Catalogs;
        internal bool Fail;
        internal Task? AssetPause;
        internal TaskCompletionSource AssetEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            string route = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
            object data;
            if (route.EndsWith("lyric/list"))
            {
                AssetEntered.TrySetResult(); if (AssetPause is { } paused) await paused; // Deliberately late transport completion.
                data = new { list = new[] { new { content = "[00:00]缓存歌词\n[00:01]同步", source = 1 } } };
            }
            else if (route.EndsWith("playlist/list")) data = new { list = Array.Empty<object>() };
            else if (route.EndsWith("album/list") || route.EndsWith("artist/list") || route.EndsWith("favorite-track/list")) data = new { total = 0, list = Array.Empty<object>() };
            else
            {
                int size = int.Parse(query["size"]), page = int.Parse(query["page"]);
                if (size == 50 && page == 1) Interlocked.Increment(ref Catalogs);
                data = new { total = Count, list = Enumerable.Range(0, Count).Skip((page - 1) * size).Take(size)
                    .Select(index => new { guid = "cached-" + index, title = "缓存歌曲 " + index, duration = 60000 }).ToArray() };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = 0, data }), Encoding.UTF8, "application/json") };
        }
    }
    private async Task VerifyMetadataWindowAsync(Action<string, bool> verify)
    {
        var oldMotion = motionPreferenceOverride; motionPreferenceOverride = false;
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHandler = new MetadataFixtureHandler { AssetPause = firstRelease.Task };
        var secondHandler = new MetadataFixtureHandler { AssetPause = secondRelease.Task, Count = 3 };
        using var first = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), firstHandler, Guid.NewGuid());
        using var second = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), secondHandler, Guid.NewGuid());
        Task? oldSync = null, currentSync = null;
        void Activate(NasApiClient api)
        {
            ResetMetadataSync(); metadataTestClient = allMusicTestClient = api; remoteCapabilityTestSource = api.SourceInstanceId;
            ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
            ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
            RequestMetadataSync();
        }
        try
        {
            Activate(first); oldSync = metadataTask;
            await firstHandler.AssetEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Task.Delay(50); Root.UpdateLayout();
            verify("metadata_activity_in_titlebar_search_row", MetadataActivity.IsActive && MetadataActivity.Visibility == Visibility.Visible
                && MetadataActivity.TransformToVisual(AppTitleBar).TransformPoint(new()).X > SearchAction.TransformToVisual(AppTitleBar).TransformPoint(new()).X);
            await ShowHomeAsync();
            verify("metadata_sync_does_not_block_home_navigation", HomePanel.Visibility == Visibility.Visible && MetadataActivity.IsActive);
            await ShowAllMusicAsync();
            verify("metadata_complete_text_available_while_assets_sync", CurrentMetadata(first)?.Tracks.Count == 2
                && ((IEnumerable<MusicTrack>)TrackList.ItemsSource).Count(track => track.SourceInstanceId == first.SourceInstanceId) == 2);
            int calls = firstHandler.Calls; firstHandler.Fail = true;
            await ShowAllMusicAsync();
            verify("metadata_cached_catalog_opens_without_http", firstHandler.Calls == calls
                && ((IEnumerable<MusicTrack>)TrackList.ItemsSource).Any(track => track.SourceInstanceId == first.SourceInstanceId));
            await ShowHomeAsync();
            verify("metadata_late_text_does_not_reopen_library", HomePanel.Visibility == Visibility.Visible && LibraryPanel.Visibility == Visibility.Collapsed);
            Activate(second); currentSync = metadataTask;
            await secondHandler.AssetEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Task.Delay(50);
            verify("metadata_account_switch_uses_only_new_source", CurrentMetadata(first) is null && CurrentMetadata(second)?.Tracks.Count == 3
                && metadataSnapshot!.Tracks.All(track => track.SourceInstanceId == second.SourceInstanceId));
            firstRelease.TrySetResult(); await oldSync!.WaitAsync(TimeSpan.FromSeconds(10));
            verify("metadata_old_completion_preserves_new_activity", MetadataActivity.IsActive && metadataSnapshot?.SourceId == second.SourceInstanceId
                && metadataSnapshot.Tracks.Count == 3 && MetadataNotice.Visibility == Visibility.Collapsed);
            secondRelease.TrySetResult(); await currentSync!.WaitAsync(TimeSpan.FromSeconds(10));
            verify("metadata_completion_stops_activity_and_notifies", !MetadataActivity.IsActive && MetadataActivity.Visibility == Visibility.Collapsed
                && MetadataNotice.Visibility == Visibility.Visible && MetadataNotice.Text == "飞牛资料同步完成");
            verify("metadata_lyrics_persist_for_new_source", (await metadataCache.ReadLyricsAsync(metadataSnapshot!.Tracks[0].Reference, default))?.Lines.Count == 2);
            secondHandler.Fail = true;
            RequestMetadataSync(refresh: true); await metadataTask!.WaitAsync(TimeSpan.FromSeconds(10));
            verify("metadata_failed_refresh_keeps_active_account_snapshot", CurrentMetadata(second)?.Tracks.Count == 3 && MetadataNotice.Text.Contains("原缓存保留"));
            secondHandler.Fail = false; secondHandler.Count = 4;
            secondHandler.AssetEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var queuedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); secondHandler.AssetPause = queuedRelease.Task;
            RequestMetadataSync(refresh: true); var queuedTask = metadataTask;
            await secondHandler.AssetEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); int catalogs = secondHandler.Catalogs;
            secondHandler.Count = 5; RequestMetadataSync(refresh: true); RequestMetadataSync(refresh: true);
            queuedRelease.TrySetResult(); await queuedTask!.WaitAsync(TimeSpan.FromSeconds(10));
            verify("metadata_refresh_requests_coalesce_into_one_followup", secondHandler.Catalogs == catalogs + 1 && CurrentMetadata(second)?.Tracks.Count == 5);
            secondHandler.Count = 0; Activate(second); await metadataTask!.WaitAsync(TimeSpan.FromSeconds(10));
            await ShowAllMusicAsync();
            verify("metadata_empty_permission_result_replaces_persisted_rows", CurrentMetadata(second)?.Tracks.Count == 0
                && ((IEnumerable<MusicTrack>)TrackList.ItemsSource).All(track => track.SourceInstanceId != second.SourceInstanceId)
                && (await metadataCache.LoadAsync(second.SourceInstanceId, default))?.Tracks.Count == 0);
            var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loadRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            allMusicBeforeCommit = () => { loadEntered.TrySetResult(); return loadRelease.Task; };
            var delayedLoad = LoadAllMusicPageAsync(1);
            try
            {
                await loadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                secondHandler.Count = 6; RequestMetadataSync(refresh: true); await metadataTask!.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { allMusicBeforeCommit = null; loadRelease.TrySetResult(); }
            await delayedLoad;
            verify("metadata_completion_during_page_load_applies_latest_snapshot", ((IEnumerable<MusicTrack>)TrackList.ItemsSource)
                .Count(track => track.SourceInstanceId == second.SourceInstanceId) == 6 && CurrentMetadata(second)?.Tracks.Count == 6);
            ResetMetadataSync();
            verify("metadata_reset_hides_activity_notice_and_snapshot", !MetadataActivity.IsActive && MetadataNotice.Visibility == Visibility.Collapsed && metadataSnapshot is null);
        }
        finally
        {
            var pending = metadataTask;
            firstRelease.TrySetResult(); secondRelease.TrySetResult(); ResetMetadataSync();
            if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(10));
            ViewModel.PlaybackRouter.Unregister(first.SourceInstanceId); ViewModel.PlaybackRouter.Unregister(second.SourceInstanceId);
            metadataTestClient = allMusicTestClient = null; allMusicBeforeCommit = null; remoteCapabilityTestSource = null; motionPreferenceOverride = oldMotion;
            UpdateSourceCommands();
        }
    }
    private async Task VerifyMetadataStandaloneWindowAsync()
    {
        var checks = new Dictionary<string, bool>(); string stage = "setup", error = "";
        void Verify(string name, bool value) { stage = name; checks[name] = value; if (!value) throw new InvalidOperationException(); }
        try { await VerifyMetadataWindowAsync(Verify); stage = "complete"; }
        catch (Exception ex) { error = ex.GetType().Name; }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "metadata-window-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks }));
    }
}
#endif
