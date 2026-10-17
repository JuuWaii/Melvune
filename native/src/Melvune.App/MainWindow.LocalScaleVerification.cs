#if DEBUG
using System.Diagnostics;
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private async Task VerifyLocalScaleWindowAsync()
    {
        motionPreferenceOverride = false; music.Player.IsMuted = true;
        try
        {
            await Services.LocalLibraryScaleVerification.RunAsync(async (source, verify, timings) =>
            {
                var saved = await localLibraryStore.LoadAsync();
                var library = new LocalLibrary(source.Source.Id, source.Settings.Directory, "万首有效音频", "规模验收");
                ApplyLocalCatalog(await localLibraryStore.SaveAsync(saved with { Libraries = [library] }));
                ReplaceLocalSource(source); remoteCapabilityTestSource = Guid.NewGuid();
                localPageMetadataReader = (_, _) => throw new IOException("Cached window must not scan metadata");
                MusicTrack[] Rows() => ((IEnumerable<MusicTrack>)TrackList.ItemsSource).ToArray();
                async Task Until(Func<bool> predicate)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    while (!predicate()) await Task.Delay(30, deadline.Token);
                }
                try
                {
                    var timer = Stopwatch.StartNew();
                    await ShowAllMusicAsync(); await Task.Delay(150); Root.UpdateLayout();
                    timings["window_first_render_seconds"] = timer.Elapsed.TotalSeconds;
                    verify("window_loads_ten_thousand_cached_valid_rows", total == 10000 && TrackList.Items.Count == 10000 && !NextPage.IsEnabled);
                    verify("window_virtualizes_valid_audio_list", TrackList.ItemsPanelRoot is ItemsStackPanel panel
                        && panel.Children.Count is > 0 and < 80 && TrackList.ContainerFromIndex(9999) is null);
                    var rows = Rows();
                    timer.Restart();
                    foreach (int index in new[] { 1000, 5000, 9999, 0 })
                    {
                        TrackList.ScrollIntoView(rows[index]); await Task.Delay(150); Root.UpdateLayout();
                        var item = TrackList.ContainerFromIndex(index) as ListViewItem;
                        verify("window_scroll_target_reachable_" + index, item is not null
                            && item.TransformToVisual(TrackList).TransformPoint(new()).Y >= -64
                            && item.TransformToVisual(TrackList).TransformPoint(new()).Y < TrackList.ActualHeight
                            && (TrackList.ItemsPanelRoot as ItemsStackPanel)?.Children.Count < 80);
                    }
                    timings["window_four_scrolls_seconds"] = timer.Elapsed.TotalSeconds;
                    verify("window_scroll_extent_matches_all_rows", Math.Abs(FindPresentationScroll(TrackList)!.ExtentHeight - 10000 * 64) < 100);
                    timer.Restart(); await StartQueueAsync(rows[^1]);
                    await Until(() => mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                        && music.Player.PlaybackSession.Position.TotalSeconds > .3);
                    timings["last_row_queue_play_seconds"] = timer.Elapsed.TotalSeconds;
                    Pause_Click(this, new RoutedEventArgs());
                    verify("last_row_plays_from_full_ten_thousand_queue", queue.Count == 10000 && queue.Current?.Reference == rows[^1].Reference
                        && music.Player.Source is not null && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
                    var audio = music.Player.Source;
                    SearchInput.Text = "Scale Artist mp3"; timer.Restart(); await SearchAsync(); Root.UpdateLayout();
                    timings["cached_search_seconds"] = timer.Elapsed.TotalSeconds;
                    verify("search_reads_cached_tags_across_all_directories", total == 2000 && Rows().All(track => track.Artist == "Scale Artist mp3"));
                    verify("search_preserves_existing_queue_and_audio", queue.Count == 10000 && ReferenceEquals(music.Player.Source, audio)
                        && queue.Current?.Reference == rows[^1].Reference);
                    SearchInput.Text = ""; await SearchAsync();
                    verify("clear_search_restores_full_catalog", total == 10000 && TrackList.Items.Count == 10000);
                    Stop_Click(this, new RoutedEventArgs()); await music.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5));
                    verify("window_stop_releases_real_media", music.Player.Source is null);
                }
                finally
                {
                    Stop_Click(this, new RoutedEventArgs()); await music.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5));
                    localPageMetadataReader = null; remoteCapabilityTestSource = null;
                    var currentCatalog = await localLibraryStore.LoadAsync();
                    ApplyLocalCatalog(await localLibraryStore.SaveAsync(saved with { Revision = currentCatalog.Revision }));
                }
            });
        }
        finally { motionPreferenceOverride = null; }
    }
}
#endif
