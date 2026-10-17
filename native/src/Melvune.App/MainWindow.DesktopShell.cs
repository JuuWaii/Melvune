using Melvune.App.Services;
using Melvune.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private SystemTrayService? systemTray;
    private GlobalHotkeyService? globalHotkeys;
    private readonly SleepTimer sleepTimer = new();
    private DispatcherQueueTimer? sleepTimerTick;
    private bool hiddenToTray, explicitDesktopExit;

    private void InitializeDesktopShell()
    {
        ShellTrayEnabled.IsOn = integrationSettings.TrayEnabled;
        ShellCloseToTray.IsOn = integrationSettings.CloseToTray;
        ShellHotkeysEnabled.IsOn = integrationSettings.GlobalHotkeysEnabled;
        globalHotkeys = new(WinRT.Interop.WindowNative.GetWindowHandle(this), QueueDesktopShellCommand);
        playbackSnapshots.Changed += DesktopSnapshotChanged;
        sleepTimerTick = DispatcherQueue.CreateTimer();
        sleepTimerTick.Interval = TimeSpan.FromSeconds(1);
        sleepTimerTick.Tick += (_, _) => PollSleepTimer();
        ApplyDesktopShellSettings();
    }

    private void QueueDesktopShellCommand(DesktopShellCommand command) =>
        DispatcherQueue.TryEnqueue(() => { if (!closed && !closingIntegration) _ = ExecuteDesktopShellCommandAsync(command); });

    private void DesktopSnapshotChanged(PlaybackSnapshot snapshot)
    {
        if (DispatcherQueue.HasThreadAccess) SyncDesktopShellState();
        else DispatcherQueue.TryEnqueue(SyncDesktopShellState);
    }

    private void SyncDesktopShellState()
    {
        if (closed || closingIntegration) return;
        var current = playbackSnapshots.Current;
        systemTray?.Update(new(current.State == Melvune.Core.PlaybackStatus.Playing,
            current.CanPlay || current.CanPause || current.CanPrevious || current.CanNext,
            integrationSettings.LyricsEnabled, integrationSettings.Lyrics?.Locked == true));
    }

    private void ApplyDesktopShellSettings()
    {
        if (closed || closingIntegration) return;
        if (integrationSettings.TrayEnabled && systemTray is null)
        {
            systemTray = new(WinRT.Interop.WindowNative.GetWindowHandle(this),
                Path.Combine(AppContext.BaseDirectory, "Assets", "Melvune.ico"), QueueDesktopShellCommand);
            systemTray.AvailabilityChanged += TrayAvailabilityChanged;
        }
        else if (!integrationSettings.TrayEnabled && systemTray is not null)
        {
            if (hiddenToTray) RestoreMainWindow();
            systemTray.AvailabilityChanged -= TrayAvailabilityChanged;
            systemTray.Dispose(); systemTray = null;
        }
        globalHotkeys?.SetEnabled(integrationSettings.GlobalHotkeysEnabled);
        // Keep an active preference switch editable during tray failure so users can select Exit.
        ShellCloseToTray.IsEnabled = integrationSettings.CloseToTray
            || (integrationSettings.TrayEnabled && systemTray?.IsAvailable == true);
        SyncDesktopShellState(); UpdateDesktopShellStatus();
    }

    private void TrayAvailabilityChanged(bool available)
    {
        if (closed || closingIntegration) return;
        if (!available && hiddenToTray) RestoreMainWindow();
        ShellCloseToTray.IsEnabled = integrationSettings.CloseToTray || (integrationSettings.TrayEnabled && available);
        UpdateDesktopShellStatus();
    }

    private void UpdateDesktopShellStatus()
    {
        string tray = !integrationSettings.TrayEnabled ? "系统托盘已关闭。"
            : systemTray?.IsAvailable == true ? "系统托盘已开启。" : "系统托盘暂不可用，收起到托盘的操作会保留窗口。";
        string hotkeys = !integrationSettings.GlobalHotkeysEnabled ? "全局快捷键已关闭。"
            : globalHotkeys?.Conflicts is { Count: > 0 } conflicts ? "快捷键被占用：" + string.Join("、", conflicts) + "；其余组合仍可使用。"
            : "全局快捷键已开启。";
        ShellStatus.Text = tray + " " + hotkeys;
    }

    private void DesktopShell_Toggled(object sender, RoutedEventArgs e)
    {
        if (closed || updatingIntegration) return;
        integrationSettings = integrationSettings with { TrayEnabled = ShellTrayEnabled.IsOn,
            CloseToTray = ShellCloseToTray.IsOn, GlobalHotkeysEnabled = ShellHotkeysEnabled.IsOn,
            CloseChoiceRemembered = ReferenceEquals(sender, ShellCloseToTray) || integrationSettings.CloseChoiceRemembered };
        if (ReferenceEquals(sender, ShellCloseToTray)) closeChoiceMadeThisSession = true;
        SaveDesktopIntegration(); ApplyDesktopShellSettings();
    }

    private bool TryHideMainWindowToTray()
    {
        if (closed || closingIntegration || explicitDesktopExit || !integrationSettings.CloseToTray
            || !integrationSettings.TrayEnabled || !MainWindowTrayAvailable) return false;
        AppWindow.Hide(); hiddenToTray = true;
        return true;
    }

    private void RestoreMainWindow()
    {
        if (closed || closingIntegration) return;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        hiddenToTray = false; Activate();
    }

    private async Task ExecuteDesktopShellCommandAsync(DesktopShellCommand command)
    {
        if (closed || closingIntegration) return;
        try
        {
            switch (command)
            {
                case DesktopShellCommand.ShowMain: RestoreMainWindow(); break;
                case DesktopShellCommand.PlayPause:
                    await ExecutePlaybackCommandAsync(new(PlaybackCommandKind.Toggle, trackGeneration)); break;
                case DesktopShellCommand.Previous:
                    await ExecutePlaybackCommandAsync(new(PlaybackCommandKind.Previous, trackGeneration)); break;
                case DesktopShellCommand.Next:
                    await ExecutePlaybackCommandAsync(new(PlaybackCommandKind.Next, trackGeneration)); break;
                case DesktopShellCommand.ToggleDesktopLyrics:
                case DesktopShellCommand.UnlockDesktopLyrics:
                    bool unlock = command == DesktopShellCommand.UnlockDesktopLyrics;
                    integrationSettings = integrationSettings with { LyricsEnabled = unlock || !integrationSettings.LyricsEnabled,
                        Lyrics = unlock ? (integrationSettings.Lyrics ?? new()) with { Locked = false } : integrationSettings.Lyrics };
                    updatingIntegration = true;
                    try { DesktopLyricsEnabled.IsOn = integrationSettings.LyricsEnabled; SyncDesktopOptions(); SyncDesktopLyricsShortcut(); }
                    finally { updatingIntegration = false; }
                    SaveDesktopIntegration(); await ApplyDesktopIntegrationAsync();
                    if (unlock) desktopLyrics?.RecoverPosition();
                    break;
                case DesktopShellCommand.Exit: await RequestDesktopExitAsync(); break;
            }
        }
        catch (Exception) { if (!closed) ShellStatus.Text = "桌面操作未完成，请重试或在主窗口操作。"; }
        finally { SyncDesktopShellState(); }
    }

    private async Task RequestDesktopExitAsync()
    {
        if (closed || closingIntegration) return;
        explicitDesktopExit = true; closingIntegration = true;
        closeChoiceDialog?.Hide();
        CloseAudioOutputs();
        CloseDesktopShell();
        await integrationGate.WaitAsync();
        try
        {
            if (informationServer is { } server) { informationServer = null; await server.DisposeAsync(); }
        }
        catch (Exception) { /* A failed optional service must not prevent exit. */ }
        finally { integrationGate.Release(); Close(); }
    }

    private void StartSleepTimer(TimeSpan duration)
    {
        if (closed) return;
        sleepTimer.Start(duration); sleepTimerTick?.Start(); UpdateSleepTimerStatus();
    }

    private void SleepTimerStart_Click(object sender, RoutedEventArgs e)
    {
        int minutes = SleepTimerDuration.SelectedIndex switch { 0 => 15, 2 => 60, 3 => 90, _ => 30 };
        StartSleepTimer(TimeSpan.FromMinutes(minutes));
    }

    private void SleepTimerCancel_Click(object sender, RoutedEventArgs e)
    {
        sleepTimer.Cancel(); sleepTimerTick?.Stop(); UpdateSleepTimerStatus();
    }

    private void PollSleepTimer()
    {
        if (closed || closingIntegration) return;
        if (sleepTimer.TryExpire())
        {
            sleepTimerTick?.Stop(); Stop_Click(this, new());
            SleepTimerStatus.Text = "定时已到，播放已停止。"; SleepTimerCancel.IsEnabled = false;
        }
        else UpdateSleepTimerStatus();
    }

    private void UpdateSleepTimerStatus()
    {
        string text = sleepTimer.Remaining is { } remaining
            ? "将在 " + TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds)).ToString(@"hh\:mm\:ss") + " 后停止播放。"
            : "未设置睡眠定时。";
        if (SleepTimerStatus.Text != text) SleepTimerStatus.Text = text;
        SleepTimerCancel.IsEnabled = sleepTimer.IsActive;
    }

    private void CloseDesktopShell()
    {
        playbackSnapshots.Changed -= DesktopSnapshotChanged;
        sleepTimer.Cancel(); sleepTimerTick?.Stop(); sleepTimerTick = null;
        globalHotkeys?.Dispose(); globalHotkeys = null;
        if (systemTray is { } tray) { systemTray = null; tray.AvailabilityChanged -= TrayAvailabilityChanged; tray.Dispose(); }
    }
}
