#if DEBUG
using FnMusic.App.Services;
using FnMusic.Core;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private async Task OpenTrayInteractionPreviewAsync()
    {
        if (!Environment.GetCommandLineArgs().Contains("--preview-tray-menu")) return;
        await Task.Delay(1000);
        systemTray?.OpenNativeMenuForVerification();
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendShellVerificationMessage(nint window, uint message, nuint wParam, nint lParam);

    private async Task VerifyDesktopShellAsync(Action<string, bool> check, MusicTrack timed)
    {
        async Task Until(Func<bool> predicate)
        { for (int i = 0; i < 200 && !predicate(); i++) await Task.Delay(50); if (!predicate()) throw new TimeoutException(); }
        check("shell_close_and_hotkeys_default_off", !integrationSettings.CloseToTray && !integrationSettings.GlobalHotkeysEnabled);
        check("shell_tray_native_icon_available", systemTray?.IsAvailable == true);
        check("shell_default_close_does_not_hide", !TryHideMainWindowToTray() && !hiddenToTray);
        globalHotkeys!.SetEnabled(false);
        foreach (var result in GlobalHotkeyVerification.Run(WinRT.Interop.WindowNative.GetWindowHandle(this)))
            check(result.Key, result.Value);

        systemTray!.Update(new(false, false, true, true));
        var disabledMenu = systemTray.ReadNativeMenuForVerification();
        check("shell_menu_empty_playback_disabled", disabledMenu.Where(item => item.Command is DesktopShellCommand.PlayPause or DesktopShellCommand.Previous or DesktopShellCommand.Next).All(item => !item.Enabled));
        check("shell_menu_locked_lyrics_unlock_enabled", disabledMenu.Single(item => item.Command == DesktopShellCommand.UnlockDesktopLyrics).Enabled
            && disabledMenu.Single(item => item.Command == DesktopShellCommand.ToggleDesktopLyrics).Checked);
        check("shell_menu_always_has_restore_and_exit", disabledMenu.Single(item => item.Command == DesktopShellCommand.ShowMain).Enabled
            && disabledMenu.Single(item => item.Command == DesktopShellCommand.Exit).Enabled);
        SyncDesktopShellState();
        systemTray.VerifyTaskbarRecreation();
        check("shell_taskbar_recreate_keeps_icon_available", systemTray.IsAvailable);

        queue.Select(timed.Reference); await PlayTrackAsync(timed); await Until(() => mediaReady);
        await ExecuteDesktopShellCommandAsync(DesktopShellCommand.PlayPause);
        await Until(() => music.Player.PlaybackSession.PlaybackState == global::Windows.Media.Playback.MediaPlaybackState.Paused);
        check("shell_pause_reuses_player", mediaReady && snapshotTrack?.Reference == timed.Reference);
        await ExecuteDesktopShellCommandAsync(DesktopShellCommand.PlayPause);
        await Until(() => music.Player.PlaybackSession.PlaybackState == global::Windows.Media.Playback.MediaPlaybackState.Playing
            && playbackSnapshots.Current.State == FnMusic.Core.PlaybackStatus.Playing);
        check("shell_resume_reuses_player", mediaReady && playbackSnapshots.Current.State == FnMusic.Core.PlaybackStatus.Playing);
        await ExecuteDesktopShellCommandAsync(DesktopShellCommand.UnlockDesktopLyrics);
        DesktopLyricsLocked.IsOn = true;
        check("shell_unlock_fixture_is_passthrough", desktopLyrics!.IsNativeInputPassThrough);
        await ExecuteDesktopShellCommandAsync(DesktopShellCommand.UnlockDesktopLyrics);
        check("shell_unlock_restores_lyrics_input", desktopLyrics is { IsLyricsVisible: true } && !desktopLyrics.IsNativeInputPassThrough
            && !DesktopLyricsLocked.IsOn && integrationSettings.Lyrics?.Locked == false);
        var source = music.Player.Source;
        ShellCloseToTray.IsOn = true;
        SendShellVerificationMessage(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x0010, 0, 0);
        await Until(() => hiddenToTray);
        check("shell_native_close_hides_without_stopping", !AppWindow.IsVisible && !closingIntegration && !closed
            && ReferenceEquals(source, music.Player.Source) && desktopLyrics.IsLyricsVisible
            && informationServer is not null && File.Exists(informationServer.DiscoveryPath));
        await ExecuteDesktopShellCommandAsync(DesktopShellCommand.ShowMain);
        check("shell_restore_keeps_playback", AppWindow.IsVisible && !hiddenToTray && ReferenceEquals(source, music.Player.Source));

        ShellTrayEnabled.IsOn = false;
        check("shell_unavailable_tray_never_hides", systemTray is null && !TryHideMainWindowToTray() && AppWindow.IsVisible && !ShellCloseToTray.IsEnabled);
        ShellTrayEnabled.IsOn = true;
        check("shell_reenabled_tray_ready", systemTray?.IsAvailable == true && ShellCloseToTray.IsEnabled);
        var persisted = JsonSerializer.Deserialize<DesktopIntegrationSettings>(File.ReadAllText(IntegrationSettingsFile))!;
        check("shell_close_preference_persists", persisted.TrayEnabled && persisted.CloseToTray && !persisted.GlobalHotkeysEnabled);
        ShellCloseToTray.IsOn = false;

        StartSleepTimer(TimeSpan.FromSeconds(1));
        SleepTimerCancel_Click(this, new()); await Task.Delay(1100); PollSleepTimer();
        check("sleep_cancel_preserves_playback", !sleepTimer.IsActive && mediaReady && ReferenceEquals(source, music.Player.Source));
        StartSleepTimer(TimeSpan.FromSeconds(1)); StartSleepTimer(TimeSpan.FromMinutes(30));
        await Task.Delay(1100); PollSleepTimer();
        check("sleep_replacement_preserves_new_deadline", sleepTimer.Remaining > TimeSpan.FromMinutes(29) && mediaReady);
        StartSleepTimer(TimeSpan.FromSeconds(1));
        ShellCloseToTray.IsOn = true; check("sleep_fixture_hidden_to_tray", TryHideMainWindowToTray());
        await Until(() => !sleepTimer.IsActive);
        check("sleep_expiry_stops_hidden_player", !mediaReady && music.Player.Source is null && hiddenToTray
            && SleepTimerStatus.Text.Contains("播放已停止") && !SleepTimerCancel.IsEnabled);
        await ExecuteDesktopShellCommandAsync(DesktopShellCommand.ShowMain); ShellCloseToTray.IsOn = false;
        await ExecuteDesktopShellCommandAsync(DesktopShellCommand.PlayPause); await Until(() => mediaReady);
        PollSleepTimer();
        check("sleep_expiry_does_not_stop_resumed_player_again", mediaReady && music.Player.Source is not null);
        Stop_Click(this, new());
    }
}
#endif
