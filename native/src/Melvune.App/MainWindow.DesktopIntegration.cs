using System.Text.Json;
using Melvune.App.Services;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private SystemMediaBridge? systemMedia;
    private DesktopLyricsWindow? desktopLyrics;
    private PlaybackInformationServer? informationServer;
    private readonly SemaphoreSlim integrationGate = new(1);
    private bool updatingIntegration = true;
    private bool closingIntegration;
    private sealed record DesktopIntegrationSettings(bool LyricsEnabled = false, bool InformationEnabled = false,
        DesktopLyricsOptions? Lyrics = null, bool SystemMediaEnabled = true,
        bool TrayEnabled = true, bool CloseToTray = false, bool GlobalHotkeysEnabled = false,
        bool CloseChoiceRemembered = false);
    private DesktopIntegrationSettings integrationSettings = new();
    private string IntegrationSettingsFile => Path.Combine(AppDataDirectory.Resolve(), "desktop-integration.json");

    private void InitializeDesktopIntegration()
    {
        try
        {
            if (File.Exists(IntegrationSettingsFile))
            {
                if (new FileInfo(IntegrationSettingsFile).Length > 8192) throw new InvalidDataException();
                integrationSettings = ReadDesktopIntegrationSettings(File.ReadAllText(IntegrationSettingsFile));
            }
        }
        catch (Exception) { integrationSettings = new(); IntegrationStatus.Text = "桌面联动设置无法读取，已使用默认值。"; }
        // Automatic MediaPlayer commands must stay off even when the persisted SMTC switch is off.
        try { music.Player.CommandManager.IsEnabled = false; }
        catch (Exception) { IntegrationStatus.Text = "SMTC 暂不可用，应用内播放仍可使用。"; }
        DesktopLyricsEnabled.IsOn = integrationSettings.LyricsEnabled;
        SyncDesktopLyricsShortcut();
        SystemMediaEnabled.IsOn = integrationSettings.SystemMediaEnabled;
        InformationEnabled.IsOn = integrationSettings.InformationEnabled;
        SyncDesktopOptions(); updatingIntegration = false;
        AppWindow.Closing += PrepareDesktopClose;
        Root.ActualThemeChanged += (_, _) => ApplyDesktopTheme();
        updatingIntegration = true;
        InitializeDesktopShell(); updatingIntegration = false;
        _ = ApplyDesktopIntegrationAsync();
    }
    private static DesktopIntegrationSettings ReadDesktopIntegrationSettings(string json)
    {
        var settings = JsonSerializer.Deserialize<DesktopIntegrationSettings>(json) ?? new();
        // An older explicit close-to-tray preference must continue working without another prompt.
        // Explicit false in the new field means a session-only choice and must not be migrated.
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(nameof(DesktopIntegrationSettings.CloseChoiceRemembered), out _)
            && settings.CloseToTray)
            settings = settings with { CloseChoiceRemembered = true };
        return settings;
    }
    private void SyncDesktopOptions()
    {
        var options = integrationSettings.Lyrics ?? new();
        DesktopLyricsTopmost.IsOn = options.Topmost; DesktopLyricsLocked.IsOn = options.Locked;
        DesktopLyricsFont.Value = Math.Clamp(options.FontSize, 16, 64);
        DesktopLyricsTheme.SelectedIndex = options.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
    }
    private void SaveDesktopIntegration()
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory.Resolve());
            string pending = IntegrationSettingsFile + "." + playbackSnapshots.InstanceId.ToString("N") + ".tmp";
            try { File.WriteAllText(pending, JsonSerializer.Serialize(integrationSettings)); File.Move(pending, IntegrationSettingsFile, true); }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        catch (Exception) { IntegrationStatus.Text = "桌面联动偏好未能保存，当前会话仍可使用。"; }
    }
    private void ApplyDesktopTheme()
    {
        var options = integrationSettings.Lyrics ?? new();
        desktopLyrics?.SetAppTheme(Root.ActualTheme);
        desktopLyrics?.ApplyOptions(options);
    }
    private async Task ApplyDesktopIntegrationAsync()
    {
        await integrationGate.WaitAsync();
        try
        {
            if (closed || closingIntegration) return;
            if (integrationSettings.SystemMediaEnabled && systemMedia is null)
            {
                try
                {
                    systemMedia = new(music.Player, WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue, playbackSnapshots, ExecutePlaybackCommandAsync);
                    if (snapshotTrack is { } track && trackGeneration is Guid generation)
                        _ = LoadSystemArtworkAsync(track, generation, work.Token);
                }
                catch (Exception) { IntegrationStatus.Text = "SMTC 暂不可用，应用内播放仍可使用。"; }
            }
            else if (!integrationSettings.SystemMediaEnabled && systemMedia is not null)
            { systemMedia.Dispose(); systemMedia = null; }
            if (integrationSettings.LyricsEnabled)
            {
                if (desktopLyrics is null)
                {
                    desktopLyrics = new(playbackSnapshots, integrationSettings.Lyrics ?? new());
                    desktopLyrics.OptionsChanged += options =>
                    {
                        if (closed || updatingIntegration) return;
                        integrationSettings = integrationSettings with { Lyrics = options };
                        updatingIntegration = true; SyncDesktopOptions(); updatingIntegration = false; SaveDesktopIntegration(); SyncDesktopShellState();
                    };
                    desktopLyrics.Hidden += () =>
                    {
                        if (closed || !integrationSettings.LyricsEnabled) return;
                        integrationSettings = integrationSettings with { LyricsEnabled = false };
                        updatingIntegration = true; DesktopLyricsEnabled.IsOn = false; SyncDesktopLyricsShortcut(); updatingIntegration = false; SaveDesktopIntegration();
                    };
                }
                ApplyDesktopTheme(); if (!desktopLyrics.IsLyricsVisible) desktopLyrics.ShowLyrics();
            }
            else desktopLyrics?.HideLyrics();
            if (integrationSettings.InformationEnabled && informationServer is null)
            {
                var server = new PlaybackInformationServer(playbackSnapshots, AppDataDirectory.Resolve());
                try { await server.StartAsync(); if (closed || closingIntegration) { await server.DisposeAsync(); return; } informationServer = server; }
                catch (Exception) { await server.DisposeAsync(); throw; }
            }
            else if (!integrationSettings.InformationEnabled && informationServer is not null)
            {
                var server = informationServer; informationServer = null; await server.DisposeAsync();
            }
        }
        catch (Exception) { IntegrationStatus.Text = "桌面联动启停失败，请重新开启后重试。"; }
        finally { integrationGate.Release(); SyncDesktopShellState(); }
    }
    private async void DesktopIntegration_Toggled(object sender, RoutedEventArgs e)
    {
        if (updatingIntegration || closed) return;
        integrationSettings = integrationSettings with { LyricsEnabled = DesktopLyricsEnabled.IsOn, InformationEnabled = InformationEnabled.IsOn,
            SystemMediaEnabled = SystemMediaEnabled.IsOn };
        SyncDesktopLyricsShortcut();
        SaveDesktopIntegration(); await ApplyDesktopIntegrationAsync();
    }
    private void SyncDesktopLyricsShortcut()
    {
        DesktopLyricsShortcut.IsChecked = integrationSettings.LyricsEnabled;
        string action = integrationSettings.LyricsEnabled ? "隐藏桌面歌词" : "显示桌面歌词";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DesktopLyricsShortcut, action);
        ToolTipService.SetToolTip(DesktopLyricsShortcut, action);
        SyncDesktopShellState();
    }
    private async void DesktopLyricsShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (closed || updatingIntegration) return;
        integrationSettings = integrationSettings with { LyricsEnabled = DesktopLyricsShortcut.IsChecked == true };
        updatingIntegration = true;
        try { DesktopLyricsEnabled.IsOn = integrationSettings.LyricsEnabled; SyncDesktopLyricsShortcut(); }
        finally { updatingIntegration = false; }
        SaveDesktopIntegration();
        await ApplyDesktopIntegrationAsync();
    }
    private void DesktopOptions_Changed(object sender, RoutedEventArgs e) => UpdateDesktopOptions();
    private void DesktopFont_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => UpdateDesktopOptions();
    private void DesktopTheme_Changed(object sender, SelectionChangedEventArgs e) => UpdateDesktopOptions();
    private void UpdateDesktopOptions()
    {
        if (updatingIntegration || closed) return;
        var options = (integrationSettings.Lyrics ?? new()) with { Topmost = DesktopLyricsTopmost.IsOn, Locked = DesktopLyricsLocked.IsOn,
            FontSize = DesktopLyricsFont.Value, Theme = DesktopLyricsTheme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" } };
        integrationSettings = integrationSettings with { Lyrics = options }; SaveDesktopIntegration(); ApplyDesktopTheme(); SyncDesktopShellState();
    }
    private void RecoverDesktopLyrics_Click(object sender, RoutedEventArgs e)
    {
        updatingIntegration = true; DesktopLyricsLocked.IsOn = false; updatingIntegration = false;
        integrationSettings = integrationSettings with { Lyrics = (integrationSettings.Lyrics ?? new()) with { Locked = false } };
        ApplyDesktopTheme(); desktopLyrics?.RecoverPosition(); SaveDesktopIntegration(); SyncDesktopShellState();
    }
    private void CloseLinkedServices()
    {
        closingIntegration = true;
        CloseDesktopShell();
        systemMedia?.Dispose(); systemMedia = null;
        desktopLyrics?.Dispose(); desktopLyrics = null;
        // Disposal cancels I/O immediately; completion stays off the UI thread.
        if (informationServer is { } server) { informationServer = null; _ = server.DisposeAsync(); }
    }
    private async void PrepareDesktopClose(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (closingIntegration) return;
        args.Cancel = true;
        await HandleMainWindowCloseAsync();
    }
}
