#if DEBUG
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Media.Playback;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private sealed record QueueSessionExpected(TrackReference[] Tracks, TrackReference? Current, QueueMode Mode,
        TrackReference[] History, TrackReference[] RequestedNext, double Position, string Origin);

    // Run save/offline/restore/mutate/removed/zero/clear/final in distinct processes sharing an isolated profile.
    // The caller closes the ordinary window after each phase. Only that normal close writes the session.
    private async Task VerifyQueueSessionWindowAsync(string phase)
    {
        var checks = new Dictionary<string, bool>();
        string stage = "setup", error = "";
        double? firstPlayingPosition = null;
        string expectedFile = Path.Combine(ThemeSettingsDirectory(), "queue-session-expected.json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        void Check(string key, bool value)
        { stage = key; checks[key] = value; if (!value) throw new InvalidOperationException(key); }
        async Task Until(Func<bool> predicate)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            while (!predicate()) await Task.Delay(30, timeout.Token);
        }
        static QueueSessionExpected Expected(PlaybackSessionState state) => new(
            state.Queue.Tracks.Select(track => track.Reference).ToArray(), state.Queue.Current, state.Queue.Mode,
            state.Queue.History.ToArray(), state.Queue.RequestedNext.ToArray(), state.PositionSeconds, state.Origin);
        void CheckSnapshot(string prefix, PlaybackQueueSnapshot actual, QueueSessionExpected expected)
        {
            Check(prefix + "_track_order_and_source_identity", actual.Tracks.Select(track => track.Reference).SequenceEqual(expected.Tracks));
            Check(prefix + "_current", actual.Current == expected.Current);
            Check(prefix + "_mode", actual.Mode == expected.Mode);
            Check(prefix + "_history_newest_first", actual.History.SequenceEqual(expected.History));
            Check(prefix + "_explicit_next_order", actual.RequestedNext.SequenceEqual(expected.RequestedNext));
        }
        void CheckSilent(string prefix)
        {
            Check(prefix + "_does_not_open_or_autoplay", !mediaReady && music.Player.Source is null
                && music.Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing);
        }
        async Task WriteExpected()
        {
            var capture = CapturePlaybackSession();
            await File.WriteAllTextAsync(expectedFile, JsonSerializer.Serialize(Expected(capture)), deadline.Token);
        }
        async Task StartAndObserve(double expectedPosition)
        {
            double observed = double.NaN;
            void Observe(MediaPlaybackSession session, object args)
            {
                if (session.PlaybackState == MediaPlaybackState.Playing)
                    Interlocked.CompareExchange(ref observed, session.Position.TotalSeconds, double.NaN);
            }
            music.Player.PlaybackSession.PlaybackStateChanged += Observe;
            try
            {
                PlayPause_Click(this, new RoutedEventArgs());
                await Until(() => mediaReady && !playbackResumeOpening && pendingPlaybackResume is null
                    && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
                await Until(() => !double.IsNaN(Volatile.Read(ref observed)));
                firstPlayingPosition = observed;
                Check("first_playing_event_uses_saved_position", observed >= Math.Max(0, expectedPosition - .3)
                    && observed < expectedPosition + 2);
                double before = music.Player.PlaybackSession.Position.TotalSeconds;
                await Until(() => music.Player.PlaybackSession.Position.TotalSeconds > before + .25);
                Check("restored_native_player_advances_while_muted", music.Player.IsMuted && music.Player.Source is not null);
                Pause_Click(this, new RoutedEventArgs());
                await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
            }
            finally { music.Player.PlaybackSession.PlaybackStateChanged -= Observe; }
        }
        async Task RemoveThroughQueueButton(TrackReference reference)
        {
            if (queuePage is null) ShowQueuePage();
            Root.UpdateLayout();
            queuePage!.List.SelectedItem = queue.Tracks.Single(track => track.Reference == reference);
            Check("remove_button_available_" + queue.Count, queueRemoveButton is { IsEnabled: true });
            ((IInvokeProvider)new ButtonAutomationPeer(queueRemoveButton!).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => queue.Tracks.All(track => track.Reference != reference));
        }
        try
        {
            music.Player.IsMuted = true; motionPreferenceOverride = false;
            Check("phase_is_known", phase is "save" or "offline" or "restore" or "mutate" or "removed" or "zero" or "clear" or "final");
            if (phase == "save")
            {
                Check("isolated_session_starts_empty", await playbackResumeStore.LoadSessionAsync(deadline.Token) is null);
                string root = Path.Combine(ThemeSettingsDirectory(), "queue-session-fixture");
                Directory.CreateDirectory(root);
                foreach (string name in new[] { "a", "b", "c" })
                {
                    // Sparse PCM data is exactly silent, including if a native mute regression occurs.
                    const int size = 48000 * 60 * 4;
                    using var file = new FileStream(Path.Combine(root, name + ".wav"), FileMode.CreateNew);
                    using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
                    writer.Write("RIFF"u8); writer.Write(size + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
                    writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(192000);
                    writer.Write((short)4); writer.Write((short)16); writer.Write("data"u8); writer.Write(size);
                    file.SetLength(size + 44);
                }
                var catalog = await localLibraryStore.LoadAsync(deadline.Token);
                Check("isolated_local_catalog_starts_empty", catalog.Libraries.Count == 0);
                var library = new LocalLibrary(Guid.NewGuid(), root, "队列恢复测试库", "队列验收");
                ApplyLocalCatalog(await localLibraryStore.SaveAsync(catalog with { Libraries = [library] }, deadline.Token));
                var source = await LocalMusicSource.ScanAsync(library.Settings, deadline.Token,
                    (path, _) => Task.FromResult(new LocalMusicMetadata("队列夹具 " + Path.GetFileNameWithoutExtension(path), "静音验证", 60)
                        { Album = "队列恢复测试" }));
                await localIndexStore.SaveAsync(source, deadline.Token); ReplaceLocalSource(source);
                var local = source.Tracks.OrderBy(track => track.Title, StringComparer.Ordinal).ToArray();
                Check("three_real_indexed_tracks", local.Length == 3);
                var remote = local[0] with { SourceInstanceId = Guid.NewGuid(), Title = "未连接的远端同 ID 歌曲", SourceLabel = "远端夹具" };
                Check("fixture_has_same_id_different_sources", remote.Id == local[0].Id && remote.Reference != local[0].Reference);
                queue.Replace([local[0], local[1], local[2], remote], local[0].Reference);
                queue.Mode = QueueMode.RepeatOne; queueOrigin = "跨音乐库队列恢复验收";
                Check("explicit_next_inserted", queue.InsertNext(local[2]));
                Check("selection_creates_nonempty_history", queue.Select(local[1].Reference)?.Reference == local[1].Reference);
                UpdateQueueModeControl(); UpdateQueueStatus();
                await PlayTrackAsync(local[1]);
                await Until(() => mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
                Pause_Click(this, new RoutedEventArgs());
                await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
                music.Player.PlaybackSession.Position = TimeSpan.FromSeconds(18);
                await Until(() => Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 18) < .15);
                await Task.Delay(150, deadline.Token); UpdateProgress();
                var capture = CapturePlaybackSession();
                Check("close_snapshot_contains_complete_mixed_queue", capture.Queue.Tracks.Count == 4
                    && capture.Queue.Current == local[1].Reference && capture.Queue.Mode == QueueMode.RepeatOne
                    && capture.Queue.History.SequenceEqual([local[0].Reference])
                    && capture.Queue.RequestedNext.SequenceEqual([local[2].Reference])
                    && capture.Origin == queueOrigin && Math.Abs(capture.PositionSeconds - 18) < .2);
                Check("normal_close_is_only_session_writer", await playbackResumeStore.LoadSessionAsync(deadline.Token) is null);
                await WriteExpected();
            }
            else if (phase == "final")
            {
                Check("previous_normal_close_removed_session_file", !File.Exists(Path.Combine(ThemeSettingsDirectory(), "playback-resume.json")));
                Check("cleared_session_does_not_load", await playbackResumeStore.LoadSessionAsync(deadline.Token) is null);
                await RestorePlaybackResumeAsync();
                Check("cleared_queue_stays_empty_after_restart", queue.Count == 0 && queue.Current is null
                    && pendingPlaybackResume is null && lastPlaybackResume is null);
                CheckSilent("cleared_restart");
            }
            else
            {
                var expected = JsonSerializer.Deserialize<QueueSessionExpected>(await File.ReadAllTextAsync(expectedFile, deadline.Token))
                    ?? throw new InvalidDataException("Missing queue expectations.");
                var saved = await playbackResumeStore.LoadSessionAsync(deadline.Token)
                    ?? throw new InvalidDataException("Missing session.");
                CheckSnapshot("previous_normal_close_disk", saved.Queue, expected);
                Check("previous_normal_close_disk_position_and_origin", Math.Abs(saved.PositionSeconds - expected.Position) < .1 && saved.Origin == expected.Origin);
                await RestorePlaybackResumeAsync(); UpdateProgress();
                CheckSnapshot("startup", queue.CaptureSnapshot(), expected);
                Check("startup_restores_queue_origin", queueOrigin == expected.Origin);
                Check("startup_updates_mode_controls", RepeatOneModeItem.IsChecked && !SequentialModeItem.IsChecked && SettingsModeSummary.Text == "单曲循环");
                CheckSilent("startup");
                Check("startup_current_and_position_presentation", expected.Current is null
                    ? pendingPlaybackResume is null && SeekSlider.Value == 0
                    : pendingPlaybackResume?.Track.Reference == expected.Current
                        && Math.Abs(SeekSlider.Value - expected.Position) < .1);
                await Task.Delay(200, deadline.Token); CheckSilent("idle");
                if (phase == "offline")
                {
                    Check("offline_current_source_is_not_registered", expected.Current is not null && !ViewModel.PlaybackRouter.CanPlay(expected.Current));
                    PlayPause_Click(this, new RoutedEventArgs()); await Task.Delay(150, deadline.Token);
                    CheckSilent("offline_play");
                    CheckSnapshot("offline_play_keeps_queue", queue.CaptureSnapshot(), expected);
                    Check("offline_play_keeps_position", pendingPlaybackResume?.PositionSeconds == expected.Position && PlaybackStatus.Text.Contains("来源未连接"));
                    ResetLibrary(); UpdateProgress();
                    CheckSnapshot("offline_reset_keeps_queue", queue.CaptureSnapshot(), expected);
                    Check("offline_reset_keeps_position", pendingPlaybackResume?.PositionSeconds == expected.Position);
                }
                else
                {
                    await RestoreLocalIndexesAtStartupAsync();
                    Check("real_persisted_local_index_restored", localSources.Values.Sum(source => source.Tracks.Count) == 3);
                    CheckSnapshot("index_restore_keeps_queue", queue.CaptureSnapshot(), expected);
                    if (phase == "restore" || phase == "zero")
                    {
                        Check("current_resolves_through_real_local_index", queue.Current is { } current && ResolveResumeTrack(current)?.Reference == expected.Current);
                        await StartAndObserve(expected.Position);
                        CheckSnapshot("play_click_keeps_queue", queue.CaptureSnapshot(), expected);
                        if (phase == "restore")
                        {
                            music.Player.PlaybackSession.Position = TimeSpan.FromSeconds(18);
                            await Until(() => Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 18) < .15);
                        }
                        await WriteExpected();
                    }
                    else if (phase == "mutate")
                    {
                        var requested = expected.RequestedNext.Single();
                        NextTrack_Click(this, new RoutedEventArgs());
                        await Until(() => queue.Current?.Reference == requested && mediaReady
                            && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
                        Pause_Click(this, new RoutedEventArgs());
                        await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
                        var next = queue.CaptureSnapshot();
                        Check("saved_explicit_next_beats_repeat_one", next.Current == requested && next.RequestedNext.Count == 0
                            && next.History.SequenceEqual(new[] { expected.Current!, expected.History.Single() }));
                        var unavailable = queue.Tracks.Single(track => !localSources.ContainsKey(track.SourceInstanceId));
                        var stillCurrent = queue.Current!.Reference;
                        await RemoveThroughQueueButton(unavailable.Reference);
                        Check("remove_noncurrent_source_qualified_track_keeps_current", queue.Count == 3 && queue.Current?.Reference == stillCurrent
                            && queue.Tracks.Any(track => track.Id == unavailable.Id && track.SourceInstanceId != unavailable.SourceInstanceId));
                        await RemoveThroughQueueButton(stillCurrent);
                        Check("remove_current_keeps_other_tracks_without_current_or_resume", queue.Count == 2 && queue.Current is null
                            && pendingPlaybackResume is null && lastPlaybackResume is null && CapturePlaybackResume() is null);
                        CheckSilent("remove_current");
                        var capture = CapturePlaybackSession();
                        Check("removed_current_close_snapshot_keeps_remaining_queue", capture.Queue.Tracks.Count == 2
                            && capture.Queue.Current is null && capture.PositionSeconds == 0 && capture.Origin == expected.Origin);
                        await WriteExpected();
                    }
                    else if (phase == "removed")
                    {
                        Check("restart_keeps_nonempty_queue_without_current", queue.Count == 2 && queue.Current is null
                            && pendingPlaybackResume is null && saved.PositionSeconds == 0);
                        var selected = queue.Tracks.Last(); queue.Select(selected.Reference);
                        await PlayTrackAsync(selected);
                        await Until(() => mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
                        Stop_Click(this, new RoutedEventArgs()); UpdateProgress();
                        Check("explicit_stop_preserves_queue_and_resets_position", queue.Count == 2 && queue.Current?.Reference == selected.Reference
                            && CapturePlaybackSession().PositionSeconds == 0 && pendingPlaybackResume is null && SeekSlider.Value == 0);
                        CheckSilent("explicit_stop"); await WriteExpected();
                    }
                    else if (phase == "clear")
                    {
                        ShowQueuePage(); Root.UpdateLayout();
                        Check("real_clear_button_is_available", queueClearButton is { IsEnabled: true });
                        ((IInvokeProvider)new ButtonAutomationPeer(queueClearButton!).GetPattern(PatternInterface.Invoke)).Invoke();
                        await Until(() => queue.Count == 0);
                        Check("clear_forgets_entire_session", queue.Current is null && pendingPlaybackResume is null
                            && lastPlaybackResume is null && CapturePlaybackResume() is null);
                        CheckSilent("clear");
                    }
                }
            }
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally { motionPreferenceOverride = null; }
        await File.WriteAllTextAsync(Path.Combine(ThemeSettingsDirectory(), $"queue-session-{phase}-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, phase, stage, error, checks, firstPlayingPosition }));
    }
}
#endif
