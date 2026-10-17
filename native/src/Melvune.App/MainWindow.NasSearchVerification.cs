#if DEBUG
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;

namespace Melvune.App;

public sealed partial class MainWindow
{
    // Real read-only NAS requests and production search/clear methods on real XAML.
    // A DEBUG commit barrier also forces completed old responses to arrive late.
    // This is a window-state probe, not mouse/keyboard or network-outage coverage.
    private async Task VerifyNasSearchWindowAsync()
    {
        var checks = new Dictionary<string, bool>();
        var timings = new Dictionary<string, long>();
        var pending = new List<Task>();
        var gates = new List<TaskCompletionSource>();
        string stage = "isolated_session", error = "";
        int catalogCount = 0, searches = 0, overlappingSubmissions = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        ViewModels.ConnectionViewModel? connection = null;
        void Verify(string name, bool value)
        { stage = name; checks[name] = value; if (!value) throw new InvalidOperationException(); }
        MusicTrack[] Rows() => (TrackList.ItemsSource as IEnumerable<MusicTrack> ?? []).ToArray();
        bool Matches(IReadOnlyList<MusicTrack> expected) => total == expected.Count
            && Rows().Select(t => t.Reference).SequenceEqual(expected.Select(t => t.Reference));
        async Task Submit(string query)
        {
            SearchInput.Text = query;
            searches++;
            await SearchFromTopAsync();
        }
        try
        {
            // Never silently fall back to the user's normal session directory.
            if (!Environment.GetCommandLineArgs().Contains("--verification-data-dir")) throw new ArgumentException();
            connection = Services.PlaybackVerification.CreateConnection();
            await connection.InitializeAsync().WaitAsync(timeout.Token);
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
            Verify("isolated_session_ready", true);
            motionPreferenceOverride = false;
            music.Player.IsMuted = true;
            allMusicTestClient = api;
            remoteCapabilityTestSource = api.SourceInstanceId;
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(api.SourceInstanceId,
                MusicSourceCapabilities.Browse | MusicSourceCapabilities.Search));
            allMusicView = true; localView = false; browseScope = BrowseScope.Nas;
            SettingsPanel.Visibility = Visibility.Collapsed;
            LibraryPanel.Visibility = Visibility.Visible;
            UpdateCollectionView(); UpdateSourceCommands();
            Verify("remote_cache_bypassed", CurrentMetadata(api) is null);

            async Task<IReadOnlyList<MusicTrack>> Read(string query) => await TrackCatalogReader.ReadAsync(
                (page, ct) => query.Length == 0 ? api.ListTracksAsync(page, 50, ct)
                    : api.SearchTracksAsync(query, page, 50, ct), timeout.Token);
            var catalog = await Read("");
            catalogCount = catalog.Count;
            Verify("nonempty_real_catalog", catalogCount > 1);
            string title = catalog[0].Title[..Math.Min(64, catalog[0].Title.Length)];
            string prefix = StringInfo.GetNextTextElement(title);
            string absent = "melvune-search-verification-" + Guid.NewGuid().ToString("N");
            var titleRows = await Read(title);
            var prefixRows = await Read(prefix);
            var emptyRows = await Read(absent);
            Verify("baseline_queries_valid", titleRows.Any(t => t.Reference == catalog[0].Reference)
                && prefixRows.Any(t => t.Reference == catalog[0].Reference) && emptyRows.Count == 0);
            var playable = catalog.Where(t => !t.IsCue).Take(2).ToArray();
            Verify("queue_fixture_ready", playable.Length == 2 && queue.Replace(playable, playable[0].Reference));
            var queueRows = queue.Tracks.Select(t => t.Reference).ToArray();
            var current = queue.Current!.Reference;

            var watch = Stopwatch.StartNew();
            for (int round = 0; round < 4; round++)
            {
                var burst = new List<Task>();
                foreach (string query in new[] { title, absent, prefix, title, prefix, absent, title, prefix })
                {
                    if (burst.Any(task => !task.IsCompleted)) overlappingSubmissions++;
                    var task = Submit(query); burst.Add(task); pending.Add(task);
                    await Task.Delay(5, timeout.Token);
                }
                await Task.WhenAll(burst).WaitAsync(timeout.Token);
                Verify("burst_" + round + "_latest_result", searchQuery == prefix && Matches(prefixRows)
                    && !PreviousPage.IsEnabled && !NextPage.IsEnabled);
            }
            timings["four_bursts_ms"] = watch.ElapsedMilliseconds;
            Verify("submissions_overlapped", overlappingSubmissions > 0);
            Verify("searches_preserve_queue", queue.Current?.Reference == current
                && queue.Tracks.Select(t => t.Reference).SequenceEqual(queueRows) && music.Player.Source is null);

            async Task Reorder(bool clear)
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                gates.Add(release);
                int arrivals = 0;
                allMusicBeforeCommit = async () =>
                {
                    if (++arrivals != 1) return;
                    entered.TrySetResult();
                    await release.Task.WaitAsync(timeout.Token);
                };
                var old = Submit(clear ? absent : title); pending.Add(old);
                await entered.Task.WaitAsync(timeout.Token);
                if (clear) await ClearTopSearchAsync().WaitAsync(timeout.Token);
                else await Submit(absent).WaitAsync(timeout.Token);
                var latestItems = TrackList.ItemsSource;
                var latestStatus = PageStatus.Text;
                Verify(clear ? "clear_before_old_response" : "empty_before_old_response",
                    searchQuery == (clear ? "" : absent) && Matches(clear ? catalog : emptyRows));
                release.TrySetResult();
                await old.WaitAsync(timeout.Token);
                Verify(clear ? "old_empty_cannot_replace_clear" : "old_results_cannot_replace_empty",
                    ReferenceEquals(latestItems, TrackList.ItemsSource) && latestStatus == PageStatus.Text
                    && Matches(clear ? catalog : emptyRows));
                allMusicBeforeCommit = null;
            }
            await Reorder(false);
            await Reorder(true);
            Verify("final_catalog_and_queue_preserved", Matches(catalog) && searchQuery.Length == 0
                && SearchInput.Text.Length == 0 && queue.Current?.Reference == current
                && queue.Tracks.Select(t => t.Reference).SequenceEqual(queueRows));
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally
        {
            libraryWork.Cancel();
            foreach (var gate in gates) gate.TrySetResult();
            allMusicBeforeCommit = null;
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(20)); checks["pending_requests_drained"] = true; }
            catch (Exception) { checks["pending_requests_drained"] = false; stage = "cleanup"; }
            if (allMusicTestClient is { } api) ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
            allMusicTestClient = null; remoteCapabilityTestSource = null;
            motionPreferenceOverride = null;
            connection?.Dispose();
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "nas-search-window-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, timings,
                catalogCount, searches, overlappingSubmissions }));
    }
}
#endif
