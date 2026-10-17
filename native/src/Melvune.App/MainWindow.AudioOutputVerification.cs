#if DEBUG
using Windows.Media.Playback;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private async Task VerifyAudioOutputsAsync(Action<string, bool> check)
    {
        async Task Until(Func<bool> predicate)
        { for (int i = 0; i < 200 && !predicate(); i++) await Task.Delay(25); if (!predicate()) throw new TimeoutException(); }
        await Until(() => activeOutputRefreshes == 0);
        audioOutputWatcher!.Changed -= AudioOutputsChanged;
        outputRefreshTimer!.Stop(); ++outputRefreshVersion;
        music.Player.Pause();
        await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
        var source = music.Player.Source; var generation = trackGeneration; var reference = queue.Current?.Reference;
        double position = music.Player.PlaybackSession.Position.TotalSeconds, volume = music.Player.Volume;
        bool muted = music.Player.IsMuted;
        var a = new OutputOption("测试设备 A", "fixture-a"); var b = new OutputOption("测试设备 B", "fixture-b");
        OutputOption selected = a; int writes = 0;
        Task<IReadOnlyList<OutputOption>> Devices(params OutputOption[] devices) => Task.FromResult<IReadOnlyList<OutputOption>>(devices);
        OutputOption UiSelection() => (OutputOption)OutputDevices.SelectedItem;
        void Select(string? id) => OutputDevices.SelectedItem = ((IEnumerable<OutputOption>)OutputDevices.ItemsSource).Single(o => o.Id == id);
        TaskCompletionSource<IReadOnlyList<OutputOption>> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            check("output_native_watcher_running", audioOutputWatcher.IsRunning);
            enumerateOutputsForVerification = () => Devices(a, b);
            readOutputForVerification = () => selected;
            writeOutputForVerification = option => { selected = option; writes++; };
            await LoadDevicesAsync(); await LoadDevicesAsync();
            check("output_refresh_preserves_explicit_without_write", writes == 0 && UiSelection().Id == a.Id);
            check("output_refresh_preserves_source_queue_clock", ReferenceEquals(music.Player.Source, source) && trackGeneration == generation
                && queue.Current?.Reference == reference && Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - position) < .1);
            check("output_refresh_preserves_pause_volume_mute", music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused
                && music.Player.Volume == volume && music.Player.IsMuted == muted);

            var slow = Pending(); enumerateOutputsForVerification = () => slow.Task;
            var oldRefresh = LoadDevicesAsync(); Select(b.Id);
            check("output_user_choice_during_refresh_applied", selected == b && writes == 1);
            outputRefreshTimer.Stop(); slow.SetResult([a]); await oldRefresh;
            check("output_old_inventory_cannot_revert_new_choice", selected == b && writes == 1 && UiSelection().Id == b.Id);

            var older = Pending(); enumerateOutputsForVerification = () => older.Task;
            var olderRefresh = LoadDevicesAsync(); enumerateOutputsForVerification = () => Devices(a, b);
            await LoadDevicesAsync(); var latestItems = OutputDevices.ItemsSource;
            older.SetResult([a]); await olderRefresh;
            check("output_latest_request_wins", ReferenceEquals(OutputDevices.ItemsSource, latestItems) && selected == b && writes == 1);

            var delayed = Pending(); enumerateOutputsForVerification = () => delayed.Task;
            var oldSuccess = LoadDevicesAsync();
            enumerateOutputsForVerification = () => Task.FromException<IReadOnlyList<OutputOption>>(new IOException());
            await LoadDevicesAsync();
            check("output_failed_enumeration_preserves_inventory", ReferenceEquals(OutputDevices.ItemsSource, latestItems) && selected == b && writes == 1);
            delayed.SetResult([a]); await oldSuccess;
            check("output_older_success_cannot_replace_new_failure", ReferenceEquals(OutputDevices.ItemsSource, latestItems) && OutputStatus.Text.Contains("无法刷新"));

            readOutputForVerification = () => throw new IOException();
            Select(a.Id);
            check("output_repeated_read_failure_contained", UiSelection().Id == b.Id && writes == 1 && OutputStatus.Text.Contains("无法确认"));
            readOutputForVerification = () => selected;
            writeOutputForVerification = option => { selected = option; writes++; readOutputForVerification = () => throw new IOException(); };
            Select(a.Id);
            check("output_write_success_read_failure_not_misreported", selected == a && UiSelection().Id == b.Id && OutputStatus.Text.Contains("无法确认"));
            readOutputForVerification = () => selected;
            enumerateOutputsForVerification = () => Devices(a, b);
            await LoadDevicesAsync();
            check("output_refresh_recovers_unconfirmed_selection", UiSelection().Id == a.Id);

            writeOutputForVerification = _ => throw new IOException(); Select(b.Id);
            check("output_switch_failure_restores_actual_selection", selected == a && UiSelection().Id == a.Id && OutputStatus.Text.Contains("无法切换"));
            enumerateOutputsForVerification = () => Devices(b); await LoadDevicesAsync();
            check("output_failed_fallback_keeps_disconnected_endpoint", selected == a && UiSelection().Id == a.Id
                && UiSelection().Name.Contains("不可用") && OutputStatus.Text.Contains("回退失败"));
            writeOutputForVerification = option => { selected = option; writes++; readOutputForVerification = () => throw new IOException(); };
            await LoadDevicesAsync();
            check("output_fallback_read_failure_not_misreported", selected.Id is null && OutputStatus.Text.Contains("无法确认"));
            readOutputForVerification = () => selected; writeOutputForVerification = option => { selected = option; writes++; };
            selected = a; await LoadDevicesAsync(); int fallbackWrites = writes;
            check("output_disconnection_falls_back_once", selected.Id is null && OutputDevices.SelectedIndex == 0 && OutputStatus.Text.Contains("已切换到系统默认"));
            await LoadDevicesAsync(); check("output_repeat_refresh_does_not_reapply_default", writes == fallbackWrites);
            enumerateOutputsForVerification = () => Devices(); await LoadDevicesAsync();
            check("output_no_devices_retains_default_and_hint", OutputDevices.Items.Count == 1 && OutputDevices.SelectedIndex == 0 && OutputStatus.Text.Contains("未发现"));
            enumerateOutputsForVerification = () => Devices(a, b); await LoadDevicesAsync();
            check("output_reconnect_keeps_default_choice", OutputDevices.Items.Count == 3 && OutputDevices.SelectedIndex == 0 && writes == fallbackWrites);

            int refreshes = 0;
            enumerateOutputsForVerification = () => { refreshes++; return Devices(a, b); };
            for (int i = 0; i < 20; i++) RequestOutputRefresh();
            await Until(() => refreshes == 1 && activeOutputRefreshes == 0); await Task.Delay(400);
            check("output_notifications_coalesced", refreshes == 1 && writes == fallbackWrites);
            check("output_status_shared_with_settings", OutputStatus.Text == SettingsOutputStatus.Text && OutputStatus.Text.Length > 0);
            delayed = Pending(); enumerateOutputsForVerification = () => delayed.Task;
            var obsolete = LoadDevicesAsync(); var currentItems = OutputDevices.ItemsSource;
            RequestOutputRefresh(); outputRefreshTimer.Stop(); delayed.SetResult([]); await obsolete;
            check("output_notification_invalidates_inflight_inventory", ReferenceEquals(OutputDevices.ItemsSource, currentItems) && writes == fallbackWrites);
            check("output_fake_changes_never_reopen_or_resume", ReferenceEquals(music.Player.Source, source) && queue.Current?.Reference == reference
                && trackGeneration == generation && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused
                && Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - position) < .1 && music.Player.Volume == volume && music.Player.IsMuted == muted);

            enumerateOutputsForVerification = null; readOutputForVerification = null; writeOutputForVerification = null;
            await LoadDevicesAsync();
            check("output_actual_enumeration_includes_default", OutputDevices.Items.Count > 1 && OutputDevices.SelectedIndex == 0);
            var actualDevice = ((IEnumerable<OutputOption>)OutputDevices.ItemsSource).First(o => o.Device is not null);
            Select(actualDevice.Id);
            check("output_actual_native_selection", music.Player.AudioDevice?.Id == actualDevice.Id && UiSelection().Id == actualDevice.Id);
            await LoadDevicesAsync();
            check("output_native_refresh_keeps_endpoint_and_source", music.Player.AudioDevice?.Id == actualDevice.Id && ReferenceEquals(music.Player.Source, source)
                && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
            Select(null);
            check("output_native_return_to_default", music.Player.AudioDevice is null && ReferenceEquals(music.Player.Source, source));
            ShellCloseToTray.IsOn = true;
            check("output_hidden_fixture", TryHideMainWindowToTray());
            await LoadDevicesAsync();
            check("output_hidden_window_keeps_monitoring", hiddenToTray && audioOutputWatcher.IsRunning
                && ReferenceEquals(music.Player.Source, source) && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
            RestoreMainWindow(); ShellCloseToTray.IsOn = false;
            var closePending = Pending(); enumerateOutputsForVerification = () => closePending.Task;
            var pendingCloseRefresh = LoadDevicesAsync(); var closeItems = OutputDevices.ItemsSource;
            var watcher = audioOutputWatcher; CloseAudioOutputs(); CloseAudioOutputs();
            closePending.SetResult([]); await pendingCloseRefresh;
            check("output_shutdown_rejects_pending_inventory", ReferenceEquals(OutputDevices.ItemsSource, closeItems) && music.Player.AudioDevice is null);
            check("output_close_disposes_watcher_and_timer", !watcher.IsRunning && audioOutputWatcher is null && outputRefreshTimer is null);
            var afterClose = OutputDevices.ItemsSource; await LoadDevicesAsync(); RequestOutputRefresh();
            check("output_after_close_no_work", ReferenceEquals(OutputDevices.ItemsSource, afterClose) && activeOutputRefreshes == 0);
        }
        finally
        {
            enumerateOutputsForVerification = null; readOutputForVerification = null; writeOutputForVerification = null;
            outputRefreshTimer?.Stop();
            if (!outputsClosed) { audioOutputWatcher!.Changed += AudioOutputsChanged; await LoadDevicesAsync(); }
        }
    }
}
#endif
