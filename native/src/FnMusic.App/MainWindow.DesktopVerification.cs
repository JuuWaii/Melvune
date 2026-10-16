#if DEBUG
using FnMusic.Core;
using FnMusic.Infrastructure;
using FnMusic.App.Services;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private async Task VerifyDesktopIntegrationAsync()
    {
        var checks = new Dictionary<string, bool>(); string stage = "smtc-api", error = ""; int errorCode = 0;
        string smtcStage = "", bridgeStage = "", errorOrigin = "";
        void Check(string key, bool value) { stage = key; checks[key] = value; if (!value) throw new InvalidOperationException(); }
        async Task Until(Func<bool> predicate)
        { for (int i = 0; i < 200 && !predicate(); i++) await Task.Delay(50); if (!predicate()) throw new TimeoutException(); }
        try
        {
            stage = "smtc-get-for-window";
            _ = global::Windows.Media.SystemMediaTransportControlsInterop.GetForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            Check("smtc_window_handle", true); stage = "smtc-command-manager";
            music.Player.CommandManager.IsEnabled = false; Check("smtc_command_manager", true);
            stage = "smtc-api-regression";
            await SystemMediaBridgeVerification.RunAsync(DispatcherQueue, WinRT.Interop.WindowNative.GetWindowHandle(this)); Check("smtc_api_regression", true);
            await DesktopLyricsWindowVerification.RunAsync(Check);
            await DesktopLyricsEfficiencyVerification.RunAsync(Check);
            VerifyPlaybackTimeEfficiency(Check);
            await VerifyDialogThemeAsync(Check);
            music.Player.IsMuted = true;
            string fixture = Path.Combine(AppDataDirectory.Resolve(), "fixture"); Directory.CreateDirectory(fixture);
            foreach (string name in new[] { "timed", "plain", "empty" })
            {
                using var wave = PlaybackVerification.CreateWave(); using var file = File.Create(Path.Combine(fixture, name + ".wav")); await wave.CopyToAsync(file);
            }
            await File.WriteAllTextAsync(Path.Combine(fixture, "timed.lrc"), "[00:00]第一行\n[00:05]第二行\n[00:20]第三行");
            await File.WriteAllTextAsync(Path.Combine(fixture, "plain.lrc"), "纯文本第一行\n纯文本第二行");
            await File.WriteAllBytesAsync(Path.Combine(fixture, "timed.png"), await ArtworkFixture.EncodeAsync(200, 200));
            var source = await LocalMusicSource.ScanAsync(new(Guid.NewGuid(), fixture), default); ReplaceLocalSource(source);
            TrackList.ItemsSource = source.Tracks;
            var timed = source.Tracks.Single(t => source.GetFilePath(t.Reference).EndsWith("timed.wav"));
            var plain = source.Tracks.Single(t => source.GetFilePath(t.Reference).EndsWith("plain.wav"));
            var empty = source.Tracks.Single(t => source.GetFilePath(t.Reference).EndsWith("empty.wav"));
            queue.Replace(source.Tracks, timed.Reference); await PlayTrackAsync(timed);
            await Until(() => mediaReady && currentLyrics.IsTimed && music.Player.PlaybackSession.Position.TotalSeconds > .2);
            UpdateProgress(); var first = playbackSnapshots.Current;
            Check("actual_clock_and_anonymous_generation", first.State == FnMusic.Core.PlaybackStatus.Playing && first.TrackGeneration.HasValue && first.PositionSeconds > 0 && first.LyricsKind == PlaybackLyricsKind.Timed);
            SystemMediaEnabled.IsOn = false;
            await Until(() => systemMedia is null);
            Check("smtc_switch_does_not_stop_audio", mediaReady && music.Player.Source is not null && !integrationSettings.SystemMediaEnabled);
            SystemMediaEnabled.IsOn = true;
            await Until(() => systemMedia is not null);
            Check("smtc_switch_recreates_current_session", integrationSettings.SystemMediaEnabled && playbackSnapshots.Current.TrackGeneration == first.TrackGeneration);
            pendingSeek = 30; draggingSeek = true; UpdateProgress();
            Check("drag_preview_is_not_actual_clock", playbackSnapshots.Current.PositionSeconds < 10); ResetSeek();
            await ExecutePlaybackCommandAsync(new(PlaybackCommandKind.Pause, trackGeneration));
            await Until(() => music.Player.PlaybackSession.PlaybackState == global::Windows.Media.Playback.MediaPlaybackState.Paused); UpdateProgress();
            Check("pause_snapshot", playbackSnapshots.Current.State == FnMusic.Core.PlaybackStatus.Paused);
            await ExecutePlaybackCommandAsync(new(PlaybackCommandKind.Seek, trackGeneration, 20));
            await Until(() => Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 20) < 1); UpdateProgress();
            Check("seek_snapshot_and_lyrics", playbackSnapshots.Current.PositionSeconds >= 19 && playbackSnapshots.Current.CurrentLine == "第三行");
            DesktopLyricsShortcut.IsChecked = true; DesktopLyricsShortcut_Click(DesktopLyricsShortcut, new());
            await Until(() => desktopLyrics is { IsLyricsVisible: true });
            Check("player_shortcut_opens_lyrics", DesktopLyricsEnabled.IsOn && integrationSettings.LyricsEnabled);
            DesktopLyricsShortcut.IsChecked = false; DesktopLyricsShortcut_Click(DesktopLyricsShortcut, new());
            await Until(() => desktopLyrics is { IsLyricsVisible: false });
            Check("player_shortcut_hides_without_stopping", !DesktopLyricsEnabled.IsOn && music.Player.Source is not null);
            await OpenNowPlayingAsync(); Root.UpdateLayout();
            Check("lyrics_page_has_visible_desktop_shortcut", ReferenceEquals(FullTransportHost.Content, TransportPanel)
                && DesktopLyricsShortcut.IsLoaded && DesktopLyricsShortcut.ActualWidth >= 32
                && PlayerOptionsPanel.Children.Contains(DesktopLyricsShortcut));
            DesktopLyricsShortcut.IsChecked = true; DesktopLyricsShortcut_Click(DesktopLyricsShortcut, new());
            await Until(() => desktopLyrics is { IsLyricsVisible: true });
            Check("lyrics_page_shortcut_opens_same_window", DesktopLyricsEnabled.IsOn && integrationSettings.LyricsEnabled);
            DesktopLyricsLocked.IsOn = true;
            Check("lyrics_settings_enable_native_passthrough", desktopLyrics!.Options.Locked && desktopLyrics.IsNativeInputPassThrough);
            RecoverDesktopLyrics_Click(this, new());
            Check("lyrics_settings_recover_restores_input", !desktopLyrics.Options.Locked && !desktopLyrics.IsNativeInputPassThrough
                && !DesktopLyricsLocked.IsOn && integrationSettings.Lyrics?.Locked == false);
            desktopLyrics!.HideLyrics();
            Check("lyrics_window_close_resets_shortcut", DesktopLyricsShortcut.IsChecked == false && !DesktopLyricsEnabled.IsOn);
            await CloseNowPlayingAsync();
            await Until(() => DesktopLyricsShortcut.IsLoaded); Root.UpdateLayout();
            Check("main_player_keeps_desktop_shortcut", PlayerLayout.Children.Contains(TransportPanel)
                && DesktopLyricsShortcut.IsLoaded && DesktopLyricsShortcut.ActualWidth >= 32);
            integrationSettings = integrationSettings with { LyricsEnabled = true, InformationEnabled = true }; await ApplyDesktopIntegrationAsync();
            Check("optional_services_enabled", desktopLyrics is not null && informationServer is not null && File.Exists(informationServer.DiscoveryPath));
            desktopLyrics!.ApplyOptions(new(Locked: true)); desktopLyrics.RecoverPosition(); desktopLyrics.HideLyrics();
            Check("lyrics_hide_keeps_player_and_disables_preference", music.Player.Source is not null && mediaReady && !integrationSettings.LyricsEnabled && !DesktopLyricsEnabled.IsOn);
            integrationSettings = integrationSettings with { LyricsEnabled = true }; await ApplyDesktopIntegrationAsync();
            Check("lyrics_setting_reopens_window", desktopLyrics.IsLyricsVisible);
            ResetLibrary(); UpdateProgress();
            Check("nas_reset_preserves_local_state", mediaReady && trackGeneration == first.TrackGeneration && playbackSnapshots.Current.Title == timed.Title && currentLyrics.IsTimed);
            // Delayed lyric commit across replacement must never repaint the new generation.
            var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
            lyricsBeforeCommit = async () => { entered.TrySetResult(); await release.Task; };
            var delayed = LoadLyricsAsync(timed); await entered.Task; lyricsBeforeCommit = null;
            queue.Select(plain.Reference); await PlayTrackAsync(plain); release.TrySetResult(); await delayed;
            await Until(() => mediaReady && currentLyrics.Lines.Count > 0); UpdateProgress();
            Check("late_lyrics_and_plain", playbackSnapshots.Current.TrackGeneration != first.TrackGeneration && playbackSnapshots.Current.LyricsKind == PlaybackLyricsKind.Plain && playbackSnapshots.Current.CurrentLineIndex == -1 && playbackSnapshots.Lyrics.Lines[0].Text == "纯文本第一行");
            await ExecutePlaybackCommandAsync(new(PlaybackCommandKind.Seek, first.TrackGeneration, 40)); UpdateProgress();
            Check("stale_command_rejected", playbackSnapshots.Current.PositionSeconds < 10);
            queue.Select(empty.Reference); await PlayTrackAsync(empty); await Until(() => mediaReady && snapshotLyricsKind == PlaybackLyricsKind.Empty); UpdateProgress();
            Check("empty_lyrics", playbackSnapshots.Current.LyricsKind == PlaybackLyricsKind.Empty);
            Stop_Click(this, new()); UpdateProgress();
            Check("stop_clears_public_identity_and_lyrics", playbackSnapshots.Current.State == FnMusic.Core.PlaybackStatus.Stopped && playbackSnapshots.Current.TrackGeneration is null && playbackSnapshots.Current.Title == "" && playbackSnapshots.Lyrics.Lines.Count == 0);
            string discovery = informationServer!.DiscoveryPath;
            integrationSettings = integrationSettings with { LyricsEnabled = false, InformationEnabled = false }; await ApplyDesktopIntegrationAsync();
            Check("disable_removes_discovery", informationServer is null && !File.Exists(discovery));
            integrationSettings = integrationSettings with { InformationEnabled = true }; await ApplyDesktopIntegrationAsync();
            Check("reenable_new_server", informationServer is not null && File.Exists(informationServer.DiscoveryPath));
            await VerifyDesktopShellAsync(Check, timed);
            queue.Select(timed.Reference); await PlayTrackAsync(timed);
            await Until(() => mediaReady && music.Player.PlaybackSession.Position.TotalSeconds > .2);
            await VerifyAudioOutputsAsync(Check);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; errorCode = ex is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : ex.HResult; errorOrigin = (ex.TargetSite?.Name ?? "") + (ex.Data.Contains("StyleIndex") ? $"({ex.Data["StyleIndex"]},{ex.Data["WindowStyle"]})" : ""); smtcStage = SystemMediaBridgeVerification.Stage; bridgeStage = SystemMediaBridge.InitializationStage; }
        finally { lyricsBeforeCommit = null; Stop_Click(this, new()); }
        await File.WriteAllTextAsync(Path.Combine(AppDataDirectory.Resolve(), "desktop-verification.json"), JsonSerializer.Serialize(new { stage, error, errorCode, errorOrigin, smtcStage, bridgeStage, checks }));
    }
}
#endif
