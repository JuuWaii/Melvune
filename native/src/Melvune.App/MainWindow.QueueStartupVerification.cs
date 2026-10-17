#if DEBUG
using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private async Task VerifyQueueStartupWindowAsync(string phase)
    {
        var checks = new Dictionary<string, bool>(); string stage = "setup", error = "";
        void Check(string name, bool value) { stage = name; checks[name] = value; if (!value) throw new InvalidOperationException(name); }
        try
        {
            music.Player.IsMuted = true; motionPreferenceOverride = false;
            var saved = await playbackResumeStore.LoadSessionAsync();
            var track = new MusicTrack("startup-edit", "启动编辑验证", "受控夹具", 60, false) { SourceInstanceId = Guid.NewGuid() };
            if (phase == "mode-save")
            {
                Check("fresh_empty_profile", saved is null && queue.Count == 0);
                queue.Replace([track], track.Reference);
                var excessive = Enumerable.Range(0, PlaybackQueue.MaximumTracks + 1).Select(i => track with { Id = "limit-" + i });
                Check("oversized_ui_queue_rejected_without_replacing_current", !TryReplacePlaybackQueue(excessive, new TrackReference(track.SourceInstanceId, "limit-0"))
                    && queue.Current?.Reference == track.Reference && PlaybackStatus.Text.Contains("10000"));
                queue.Clear(); QueueMode_Click(RepeatAllModeItem, new RoutedEventArgs());
                Check("empty_explicit_mode_is_captured", CapturePlaybackSession().Queue is { Mode: QueueMode.RepeatAll, Current: null } state && state.Tracks.Count == 0);
            }
            else if (phase == "edit-before")
            {
                Check("empty_mode_survived_normal_exit", saved?.Queue is { Mode: QueueMode.RepeatAll, Current: null } previous && previous.Tracks.Count == 0);
                QueueMode_Click(ShuffleModeItem, new RoutedEventArgs()); await RestorePlaybackResumeAsync();
                Check("mode_edit_before_restore_is_not_overwritten", queue.Mode == QueueMode.Shuffle && queue.Count == 0 && pendingPlaybackResume is null);
            }
            else if (phase == "edit-during")
            {
                Check("changed_empty_mode_survived_exit", saved?.Queue.Mode == QueueMode.Shuffle && saved.Queue.Tracks.Count == 0);
                queueSessionAfterRead = () =>
                {
                    QueueMode_Click(RepeatOneModeItem, new RoutedEventArgs());
                    if (!TryInsertPlaybackNext(track)) throw new InvalidOperationException("next edit failed");
                    queueOrigin = "启动编辑验证";
                };
                await RestorePlaybackResumeAsync(); queueSessionAfterRead = null;
                Check("late_restore_preserves_mode_next_and_new_queue", queue.Mode == QueueMode.RepeatOne && queue.Count == 1 && queue.Current?.Reference == track.Reference
                    && queueOrigin == "启动编辑验证" && pendingPlaybackResume is null);
            }
            else if (phase == "edit-restore")
            {
                Check("edited_queue_survived_normal_exit", saved?.Queue.Mode == QueueMode.RepeatOne && saved.Queue.Tracks.Count == 1 && saved.Queue.Current is not null);
                await RestorePlaybackResumeAsync();
                Check("edited_current_restores_silently", queue.Current?.Reference == saved!.Queue.Current && pendingPlaybackResume?.PositionSeconds == 0
                    && queue.Mode == QueueMode.RepeatOne && queueOrigin == "启动编辑验证");
                PlayPause_Click(this, new RoutedEventArgs()); await Task.Delay(100);
                Check("unavailable_edit_is_retained", queue.Count == 1 && pendingPlaybackResume?.Track.Reference == queue.Current?.Reference && PlaybackStatus.Text.Contains("来源未连接"));
                ShowQueuePage(); Root.UpdateLayout();
                ((IInvokeProvider)new ButtonAutomationPeer(queueClearButton!).GetPattern(PatternInterface.Invoke)).Invoke();
                for (int i = 0; i < 40 && queue.Count != 0; i++) await Task.Delay(25);
                Check("explicit_clear_cancels_empty_mode_record", queue.Count == 0 && queueSessionExplicitlyCleared && pendingPlaybackResume is null);
            }
            else if (phase == "mode-queue")
            {
                Check("legacy_queue_fixture_was_saved_as_v2", saved?.Queue is { Mode: QueueMode.Sequential } previous && previous.Tracks.Count == 1 && saved.PositionSeconds == 17);
                QueueMode_Click(RepeatAllModeItem, new RoutedEventArgs()); await RestorePlaybackResumeAsync();
                Check("startup_mode_override_keeps_restored_queue_and_position", queue.Count == 1 && queue.Current?.Reference == saved!.Queue.Current
                    && queue.Mode == QueueMode.RepeatAll && pendingPlaybackResume?.PositionSeconds == 17);
            }
            else if (phase == "migration")
            {
                Check("legacy_fixture_profile_is_new", saved is null);
                await playbackResumeStore.SaveAsync(new(track, 17));
                await RestorePlaybackResumeAsync(); UpdateProgress();
                Check("legacy_single_song_migrates_to_full_queue", queue.Count == 1 && queue.Current?.Reference == track.Reference && queue.Mode == QueueMode.Sequential
                    && pendingPlaybackResume?.PositionSeconds == 17 && SeekSlider.Value == 17 && CapturePlaybackSession().Queue.Tracks.Count == 1);
            }
            else throw new ArgumentException("Unknown startup phase");
            Check("startup_checks_never_open_or_autoplay_audio", music.Player.Source is null && !mediaReady);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name + ":" + ex.Message; }
        finally { queueSessionAfterRead = null; motionPreferenceOverride = null; }
        await File.WriteAllTextAsync(Path.Combine(ThemeSettingsDirectory(), "queue-startup-" + phase + "-verification.json"), JsonSerializer.Serialize(new { stage, error, checks }));
    }
}
#endif
