#if DEBUG
using System.Text.Json;
using FnMusic.App.Services;
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Media.Playback;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    // Each phase runs in a new process against one explicitly supplied, isolated profile.
    // The caller closes the real window after this returns; no verification-only save is used.
    private async Task VerifyPlaybackResumeWindowAsync(string phase)
    {
        var checks = new Dictionary<string, bool>();
        string stage = "setup", error = "";
        double? expectedPosition = null, firstPlayingPosition = null;
        TrackReference? expectedReference = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        void Verify(string name, bool value)
        { stage = name; checks[name] = value; if (!value) throw new InvalidOperationException(name); }
        async Task Until(Func<bool> predicate)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!predicate()) await Task.Delay(30, timeout.Token);
        }
        void VerifyPending(PlaybackResumeState saved)
        {
            UpdateProgress();
            Verify("startup_restores_source_qualified_song", pendingPlaybackResume?.Track.Reference == saved.Track.Reference
                && queue.Current?.Reference == saved.Track.Reference && NowPlaying.Text.Contains(saved.Track.Title));
            Verify("startup_displays_saved_progress", Math.Abs(SeekSlider.Value - saved.PositionSeconds) < .1
                && SeekSlider.Maximum >= saved.PositionSeconds && PlaybackTime.Text == $"{TimeSpan.FromSeconds(saved.PositionSeconds):mm\\:ss}");
            Verify("startup_does_not_open_or_autoplay", !mediaReady && music.Player.Source is null
                && music.Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing);
            PublishPlaybackSnapshot();
            var snapshot = playbackSnapshots.Current;
            Verify("pending_resume_public_snapshot_keeps_paused_song_and_position", snapshot.State == FnMusic.Core.PlaybackStatus.Paused
                && snapshot.TrackGeneration.HasValue && snapshot.Title == saved.Track.Title
                && Math.Abs(snapshot.PositionSeconds - saved.PositionSeconds) < .1);
            Verify("pending_resume_smtc_play_matches_source_availability", SystemMediaBridge.CanExecute(snapshot,
                new(PlaybackCommandKind.Play, snapshot.TrackGeneration)) == (ResolveResumeTrack(saved.Track) is not null));
            Verify("pending_resume_smtc_disables_seek_and_pause", !snapshot.CanSeek && !snapshot.CanPause
                && !SystemMediaBridge.CanExecute(snapshot, new(PlaybackCommandKind.Pause, snapshot.TrackGeneration))
                && !SystemMediaBridge.CanExecute(snapshot, new(PlaybackCommandKind.Seek, snapshot.TrackGeneration, 30)));
        }
        try
        {
            music.Player.IsMuted = true; motionPreferenceOverride = false;
            Verify("phase_is_known", phase is "save" or "offline" or "restore" or "stop" or "zero" or "clear");
            if (phase == "save")
            {
                string root = Path.Combine(ThemeSettingsDirectory(), "playback-resume-fixture");
                Directory.CreateDirectory(root);
                string file = Path.Combine(root, "resume.wav");
                using (var wave = PlaybackVerification.CreateWave())
                using (var output = new FileStream(file, FileMode.CreateNew)) await wave.CopyToAsync(output, deadline.Token);
                var catalog = await localLibraryStore.LoadAsync(deadline.Token);
                Verify("seed_uses_empty_isolated_catalog", catalog.Libraries.Count == 0);
                var library = new LocalLibrary(Guid.NewGuid(), root, "进度恢复测试库", "恢复验收");
                ApplyLocalCatalog(await localLibraryStore.SaveAsync(catalog with { Libraries = [library] }, deadline.Token));
                var source = await LocalMusicSource.ScanAsync(library.Settings, deadline.Token,
                    (_, _) => Task.FromResult(new LocalMusicMetadata("进度恢复夹具", "静音验证", 60) { Album = "恢复测试" }));
                await localIndexStore.SaveAsync(source, deadline.Token); ReplaceLocalSource(source);
                var track = source.Tracks.Single(); expectedReference = track.Reference;
                queue.Replace([track], track.Reference); UpdateQueueStatus();
                await PlayTrackAsync(track);
                await Until(() => mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
                Pause_Click(this, new RoutedEventArgs());
                await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
                music.Player.PlaybackSession.Position = TimeSpan.FromSeconds(18);
                await Until(() => Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 18) < .2);
                await Task.Delay(250, deadline.Token);
                UpdateProgress();
                Verify("actual_native_player_paused_at_saved_position", mediaReady && music.Player.Source is not null
                    && music.Player.IsMuted && Math.Abs(SeekSlider.Value - 18) < .2);
                var captured = CapturePlaybackResume();
                Verify("close_snapshot_uses_actual_player_position", captured?.Track.Reference == track.Reference
                    && Math.Abs(captured.PositionSeconds - 18) < .2);
                Verify("no_premature_record_written", await playbackResumeStore.LoadAsync(deadline.Token) is null);
                expectedPosition = captured!.PositionSeconds;
            }
            else
            {
                var saved = await playbackResumeStore.LoadAsync(deadline.Token) ?? throw new InvalidDataException("No saved fixture.");
                expectedReference = saved.Track.Reference; expectedPosition = saved.PositionSeconds;
                Verify("previous_process_record_has_expected_position", phase == "zero"
                    ? saved.PositionSeconds == 0 : phase == "clear"
                        ? saved.PositionSeconds is >= 0 and < 2 : Math.Abs(saved.PositionSeconds - 18) < 2);
                await RestorePlaybackResumeAsync(); VerifyPending(saved);
                await Task.Delay(500, deadline.Token);
                Verify("idle_startup_remains_silent_and_retains_position", music.Player.Source is null
                    && pendingPlaybackResume?.PositionSeconds == saved.PositionSeconds);
                if (phase == "offline")
                {
                    Verify("fixture_source_is_not_registered", !ViewModel.PlaybackRouter.CanPlay(saved.Track.Reference));
                    PlayPause_Click(this, new RoutedEventArgs());
                    await Task.Delay(150, deadline.Token);
                    Verify("unavailable_source_play_keeps_pending_progress", pendingPlaybackResume?.PositionSeconds == saved.PositionSeconds
                        && !mediaReady && music.Player.Source is null && PlaybackStatus.Text.Contains("来源未连接"));
                    ResetLibrary(); UpdateProgress();
                    Verify("connection_reset_does_not_erase_pending_resume", pendingPlaybackResume?.Track.Reference == saved.Track.Reference
                        && Math.Abs(SeekSlider.Value - saved.PositionSeconds) < .1
                        && CapturePlaybackResume()?.PositionSeconds == saved.PositionSeconds && music.Player.Source is null);
                }
                else
                {
                    await RestoreLocalIndexesAtStartupAsync();
                    Verify("source_restores_from_real_persisted_local_index", ViewModel.PlaybackRouter.CanPlay(saved.Track.Reference)
                        && ResolveResumeTrack(saved.Track)?.Reference == saved.Track.Reference);
                    VerifyPending(saved);
                    if (phase == "clear")
                    {
                        ShowQueuePage(); Root.UpdateLayout();
                        Verify("real_queue_clear_button_is_available", queueClearButton is { IsEnabled: true });
                        ((IInvokeProvider)new ButtonAutomationPeer(queueClearButton!).GetPattern(PatternInterface.Invoke)).Invoke();
                        await Until(() => queue.Count == 0);
                        Verify("real_queue_clear_forgets_resume_song_and_position", queue.Current is null
                            && pendingPlaybackResume is null && lastPlaybackResume is null && CapturePlaybackResume() is null
                            && !mediaReady && music.Player.Source is null);
                        expectedPosition = null; expectedReference = null;
                    }
                    else if (phase == "stop")
                    {
                        Stop_Click(this, new RoutedEventArgs()); UpdateProgress();
                        Verify("explicit_stop_resets_saved_position", pendingPlaybackResume is null
                            && CapturePlaybackResume() is { PositionSeconds: 0 } stopped && stopped.Track.Reference == saved.Track.Reference
                            && music.Player.Source is null && SeekSlider.Value == 0);
                        expectedPosition = 0;
                    }
                    else
                    {
                        double observed = double.NaN;
                        void Observe(MediaPlaybackSession sender, object args)
                        {
                            if (sender.PlaybackState == MediaPlaybackState.Playing)
                                Interlocked.CompareExchange(ref observed, sender.Position.TotalSeconds, double.NaN);
                        }
                        music.Player.PlaybackSession.PlaybackStateChanged += Observe;
                        try
                        {
                            // Exercise the same handler as the visible playback button.
                            PlayPause_Click(this, new RoutedEventArgs());
                            await Until(() => mediaReady && !playbackResumeOpening && pendingPlaybackResume is null
                                && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
                            await Until(() => !double.IsNaN(Volatile.Read(ref observed)));
                            firstPlayingPosition = observed;
                            Verify("first_playing_event_starts_at_restored_position", phase == "zero"
                                ? observed is >= 0 and < 2 : observed >= saved.PositionSeconds - .3 && observed < saved.PositionSeconds + 2);
                            Verify("play_click_consumes_resume_without_changing_song", pendingPlaybackResume is null
                                && queue.Current?.Reference == saved.Track.Reference && snapshotTrack?.Reference == saved.Track.Reference);
                            double before = music.Player.PlaybackSession.Position.TotalSeconds;
                            await Until(() => music.Player.PlaybackSession.Position.TotalSeconds > before + .3);
                            Verify("native_playback_advances_from_restored_position", music.Player.Source is not null && music.Player.IsMuted);
                            Pause_Click(this, new RoutedEventArgs());
                            await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
                            UpdateProgress();
                            var captured = CapturePlaybackResume();
                            Verify("next_close_captures_current_resumed_position", captured?.Track.Reference == saved.Track.Reference
                                && captured.PositionSeconds >= saved.PositionSeconds
                                && Math.Abs(SeekSlider.Value - captured.PositionSeconds) < .1);
                            expectedPosition = captured!.PositionSeconds;
                        }
                        finally { music.Player.PlaybackSession.PlaybackStateChanged -= Observe; }
                    }
                }
            }
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally { motionPreferenceOverride = null; }
        await File.WriteAllTextAsync(Path.Combine(ThemeSettingsDirectory(), $"playback-resume-{phase}-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, phase, stage, error, checks,
                expectedPosition, expectedReference, firstPlayingPosition }));
    }
}
#endif
