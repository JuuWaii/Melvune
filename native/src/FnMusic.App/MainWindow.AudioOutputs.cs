using FnMusic.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private sealed record OutputOption(string Name, string? Id, DeviceInformation? Device = null);
    private static readonly OutputOption DefaultOutput = new("系统默认设备", null);
    private AudioOutputDeviceWatcher? audioOutputWatcher;
    private DispatcherQueueTimer? outputRefreshTimer;
    private readonly CancellationTokenSource outputLifetime = new();
    private long outputRefreshVersion;
    private int activeOutputRefreshes;
    private bool updatingOutput, outputsClosed;
    private OutputOption lastConfirmedOutput = DefaultOutput;
#if DEBUG
    private Func<Task<IReadOnlyList<OutputOption>>>? enumerateOutputsForVerification;
    private Func<OutputOption>? readOutputForVerification;
    private Action<OutputOption>? writeOutputForVerification;
#endif

    private void InitializeAudioOutputs()
    {
        ReplaceOutputList([], DefaultOutput);
        outputRefreshTimer = DispatcherQueue.CreateTimer();
        outputRefreshTimer.Interval = TimeSpan.FromMilliseconds(300);
        outputRefreshTimer.IsRepeating = false;
        outputRefreshTimer.Tick += OutputRefresh_Tick;
        audioOutputWatcher = new();
        audioOutputWatcher.Changed += AudioOutputsChanged;
        EnsureAudioOutputWatcher();
    }

    private bool OutputsUnavailable => closed || closingIntegration || outputsClosed;

    private void EnsureAudioOutputWatcher()
    {
        if (OutputsUnavailable || audioOutputWatcher is null || audioOutputWatcher.IsRunning) return;
        try { audioOutputWatcher.Start(); }
        catch (Exception) { SetOutputStatus("自动检测暂不可用，请使用刷新设备重试。"); }
    }

    // Native callbacks only post work. No UI access or synchronous waits on their thread.
    private void AudioOutputsChanged() => DispatcherQueue.TryEnqueue(() => RequestOutputRefresh());

    private void RequestOutputRefresh()
    {
        if (OutputsUnavailable) return;
        ++outputRefreshVersion; // Invalidate an enumeration already in flight immediately.
        outputRefreshTimer?.Stop(); outputRefreshTimer?.Start();
    }

    private async void OutputRefresh_Tick(DispatcherQueueTimer sender, object args) => await LoadDevicesAsync();
    private async void Devices_Click(object sender, RoutedEventArgs e) => await LoadDevicesAsync();

    private async Task<IReadOnlyList<OutputOption>> EnumerateAudioOutputsAsync()
    {
#if DEBUG
        if (enumerateOutputsForVerification is { } enumerate) return await enumerate();
#endif
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector()).AsTask(outputLifetime.Token);
        return devices.Where(device => device.IsEnabled)
            .Select(device => new OutputOption(device.Name, device.Id, device)).ToArray();
    }

    private OutputOption ReadAudioOutput()
    {
#if DEBUG
        if (readOutputForVerification is { } read) return lastConfirmedOutput = read();
#endif
        var device = music.Player.AudioDevice;
        return lastConfirmedOutput = device is null ? DefaultOutput : new(device.Name, device.Id, device);
    }

    private bool TryReadAudioOutput(out OutputOption actual)
    {
        try { actual = ReadAudioOutput(); return true; }
        catch (Exception) { actual = lastConfirmedOutput; return false; }
    }

    private void WriteAudioOutput(OutputOption option)
    {
#if DEBUG
        if (writeOutputForVerification is { } write) { write(option); return; }
#endif
        music.Player.AudioDevice = option.Device;
    }

    private async Task LoadDevicesAsync()
    {
        if (OutputsUnavailable) return;
        outputRefreshTimer?.Stop(); EnsureAudioOutputWatcher();
        long version = ++outputRefreshVersion;
        activeOutputRefreshes++;
        try
        {
            var devices = await EnumerateAudioOutputsAsync();
            if (OutputsUnavailable || version != outputRefreshVersion) return;
            // Read after enumeration: the user may have changed the endpoint while awaiting it.
            if (!TryReadAudioOutput(out var actual))
            { SetOutputStatus("无法确认当前音频输出，请刷新设备后重试。"); return; }
            bool disappeared = actual.Id is not null && !devices.Any(device => device.Id == actual.Id);
            bool fallbackFailed = false;
            if (disappeared)
            {
                try { WriteAudioOutput(DefaultOutput); }
                catch (Exception) { /* Confirm the actual endpoint before describing the result. */ }
                if (!TryReadAudioOutput(out actual))
                { ReplaceOutputList(devices, actual); SetOutputStatus("回退后无法确认当前音频输出，请刷新设备后重试。"); return; }
                fallbackFailed = actual.Id is not null;
            }
            ReplaceOutputList(devices, actual);
            SetOutputStatus(fallbackFailed ? "原输出设备已断开，回退失败。请连接设备或选择系统默认设备重试。"
                : disappeared ? "原输出设备已断开，已切换到系统默认设备。"
                : devices.Count == 0 ? "未发现可用输出设备；连接设备后会自动刷新。"
                : audioOutputWatcher?.IsRunning == true ? "设备变更会自动刷新；系统默认设备跟随 Windows。"
                : "自动检测暂不可用，可手动刷新设备列表。");
        }
        catch (OperationCanceledException) when (OutputsUnavailable) { }
        catch (Exception)
        {
            if (!OutputsUnavailable && version == outputRefreshVersion)
                SetOutputStatus("无法刷新音频设备，已保留原选择。请检查设备连接后重试。");
        }
        finally { activeOutputRefreshes--; }
    }

    private void ReplaceOutputList(IReadOnlyList<OutputOption> devices, OutputOption actual)
    {
        var options = new List<OutputOption> { DefaultOutput };
        options.AddRange(devices.Where(option => option.Id is not null).DistinctBy(option => option.Id));
        var selected = options.FirstOrDefault(option => option.Id == actual.Id);
        if (selected is null) { selected = actual with { Name = actual.Name + "（不可用）" }; options.Add(selected); }
        updatingOutput = true;
        try { OutputDevices.ItemsSource = options; OutputDevices.SelectedItem = selected; }
        finally { updatingOutput = false; }
        UpdateOutputDeviceHint(selected);
    }

    private void Output_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (OutputsUnavailable || updatingOutput || OutputDevices.SelectedItem is not OutputOption option) return;
        if (activeOutputRefreshes > 0) RequestOutputRefresh();
        else ++outputRefreshVersion;
        try
        {
            if (!TryReadAudioOutput(out var actual))
            { RestoreOutputSelection(actual); SetOutputStatus("无法确认当前音频输出，请刷新设备后重试。"); return; }
            if (actual.Id != option.Id) WriteAudioOutput(option);
            actual = ReadAudioOutput();
            RestoreOutputSelection(actual);
            SetOutputStatus(actual.Id == option.Id ? "音频输出已切换。" : "切换未完成，已保留实际输出设备。请刷新后重试。");
        }
        catch (Exception)
        {
            bool confirmed = TryReadAudioOutput(out var actual);
            RestoreOutputSelection(actual);
            SetOutputStatus(confirmed ? "无法切换音频设备，已保留实际输出。请刷新设备后重试。"
                : "切换后无法确认当前音频输出，请刷新设备后重试。");
        }
    }

    private void RestoreOutputSelection(OutputOption actual)
    {
        var options = (OutputDevices.ItemsSource as IEnumerable<OutputOption>)?.Where(option => option.Id is not null).ToArray() ?? [];
        ReplaceOutputList(options, actual);
    }

    private void UpdateOutputDeviceHint(OutputOption selected)
    {
        string hint = "音频输出设备：" + selected.Name;
        ToolTipService.SetToolTip(OutputDeviceButton, hint);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OutputDeviceButton, hint);
    }

    private void SetOutputStatus(string status)
    { OutputStatus.Text = status; SettingsOutputStatus.Text = status; }

    private void CloseAudioOutputs()
    {
        if (outputsClosed) return;
        outputsClosed = true; ++outputRefreshVersion;
        if (outputRefreshTimer is { } timer) { timer.Stop(); timer.Tick -= OutputRefresh_Tick; outputRefreshTimer = null; }
        if (audioOutputWatcher is { } watcher) { watcher.Changed -= AudioOutputsChanged; watcher.Dispose(); audioOutputWatcher = null; }
        outputLifetime.Cancel(); outputLifetime.Dispose();
    }
}
