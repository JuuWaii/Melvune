#if DEBUG
using FnMusic.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace FnMusic.App;

/// <summary>Invoked by the consolidated window regression; does not control the real player.</summary>
internal static class DesktopLyricsWindowVerification
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    private static bool HasNoNativeFrame(DesktopLyricsWindow window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var point = new NativePoint();
        return (GetWindowLongPtr(handle, -16).ToInt64() & 0x00C40000) == 0
            && GetWindowRect(handle, out var outer) && GetClientRect(handle, out var client) && ClientToScreen(handle, ref point)
            && point.X == outer.Left && point.Y == outer.Top
            && client.Right == outer.Right - outer.Left && client.Bottom == outer.Bottom - outer.Top;
    }
    private static bool IsPassThrough(DesktopLyricsWindow window) =>
        (GetWindowLongPtr(WinRT.Interop.WindowNative.GetWindowHandle(window), -20).ToInt64() & 0x80020) == 0x80020;

    internal static async Task RunAsync(Action<string, bool> verify)
    {
        var hub = new PlaybackSnapshotHub();
        using var window = new DesktopLyricsWindow(hub, new DesktopLyricsOptions());
        var generation = Guid.NewGuid();
        var current = hub.Current with { State = PlaybackStatus.Playing, TrackGeneration = generation,
            Title = "桌面歌词回归", LyricsKind = PlaybackLyricsKind.Timed, CurrentLineIndex = 0,
            CurrentLine = "第一行", DurationSeconds = 120 };
        window.ShowLyrics();
        hub.Publish(current);
        await Task.Delay(80);
        verify("desktop_lyrics_real_snapshot_line", window.DisplayedLyrics == "第一行");
        verify("desktop_lyrics_native_surface_is_transparent", window.HasTransparentSurface);
        verify("desktop_lyrics_unlocked_has_no_native_frame", HasNoNativeFrame(window));
        verify("desktop_lyrics_text_has_contrasting_outline", window.HasContrastingOutline);
        hub.Publish(current with { State = PlaybackStatus.Paused, PositionSeconds = 10, CurrentLine = "暂停位置" });
        await Task.Delay(80);
        verify("desktop_lyrics_pause_keeps_actual_line", window.DisplayedLyrics == "暂停位置" && window.DisplayedStatus.Contains("已暂停"));
        hub.SetLyrics(generation, PlaybackLyricsKind.Plain, Lyrics.Parse("纯文本一\n纯文本二"));
        hub.Publish(current with { LyricsKind = PlaybackLyricsKind.Plain });
        await Task.Delay(80);
        verify("desktop_lyrics_plain_explains_missing_timeline", window.DisplayedLyrics.Contains("纯文本二") && window.DisplayedStatus.Contains("无时间轴"));
        hub.Publish(current with { TrackGeneration = Guid.NewGuid(), LyricsKind = PlaybackLyricsKind.Plain });
        await Task.Delay(80);
        verify("desktop_lyrics_late_generation_isolated", window.DisplayedLyrics == "正在读取歌词…");
        hub.Publish(current with { LyricsKind = PlaybackLyricsKind.Empty });
        await Task.Delay(80);
        verify("desktop_lyrics_empty_clear", window.DisplayedLyrics == "暂无歌词");
        hub.Publish(current with { LyricsKind = PlaybackLyricsKind.Loading });
        await Task.Delay(80);
        verify("desktop_lyrics_loading_clear", window.DisplayedLyrics == "正在读取歌词…");
        window.ApplyOptions(window.Options with { Locked = true, Topmost = true, Theme = "dark", FontSize = double.NaN });
        var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
        verify("desktop_lyrics_lock_and_bounded_options", !presenter.HasTitleBar && !presenter.IsResizable
            && presenter.IsAlwaysOnTop && window.Options.FontSize == 28);
        verify("desktop_lyrics_locked_native_mouse_passthrough", IsPassThrough(window) && HasNoNativeFrame(window));
        verify("desktop_lyrics_locked_status_explains_settings_unlock", window.DisplayedStatus.Contains("设置") && !window.DisplayedStatus.Contains("右键解锁"));
        // A pass-through window cannot receive pointer/menu commands; the main settings page calls ApplyOptions.
        window.ApplyOptions(window.Options with { Locked = false });
        verify("desktop_lyrics_settings_unlock_restores_mouse_input", !window.Options.Locked && !IsPassThrough(window) && HasNoNativeFrame(window));
        var lockSetting = window.QuickSettings.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text.StartsWith("锁定"));
        lockSetting.IsChecked = true;
        verify("desktop_lyrics_unlocked_quick_menu_can_enable_passthrough", window.Options.Locked && IsPassThrough(window));
        window.ApplyOptions(window.Options with { Locked = false });
        var topmostSetting = window.QuickSettings.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == "置顶");
        topmostSetting.IsChecked = false;
        verify("desktop_lyrics_context_menu_topmost", !window.Options.Topmost && !presenter.IsAlwaysOnTop);
        var colorSettings = window.QuickSettings.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Text == "歌词颜色");
        colorSettings.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == "浅色文字").IsChecked = true;
        window.SetAppTheme(ElementTheme.Light);
        verify("desktop_lyrics_custom_color_survives_app_theme", window.Options.Theme == "dark" && window.EffectiveTheme == ElementTheme.Dark);
        colorSettings.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == "跟随主窗口").IsChecked = true;
        verify("desktop_lyrics_system_color_follows_app_without_mutating_preference", window.Options.Theme == "system" && window.EffectiveTheme == ElementTheme.Light);
        window.ApplyOptions(window.Options with { Locked = false, Theme = "light", X = int.MinValue, Y = int.MinValue });
        window.RecoverPosition();
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var position = window.AppWindow.Position; var size = window.AppWindow.Size;
        verify("desktop_lyrics_unlock_and_recover", !presenter.HasTitleBar && !presenter.IsResizable && !IsPassThrough(window)
            && position.X >= area.X && position.Y >= area.Y
            && (long)position.X + size.Width <= (long)area.X + area.Width
            && (long)position.Y + size.Height <= (long)area.Y + area.Height);
        int hidden = 0; window.Hidden += () => hidden++;
        // WM_CLOSE exercises the native title-bar close affordance; Window.Close destroys directly.
        SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x0010, 0, 0);
        await Task.Delay(80);
        verify("desktop_lyrics_close_hides_only", !window.IsLyricsVisible && hidden == 1 && hub.Current.State == PlaybackStatus.Playing);
        window.ShowLyrics();
        verify("desktop_lyrics_can_reopen", window.IsLyricsVisible);
        await Task.Delay(80);
        verify("desktop_lyrics_reopen_restores_transparent_surface", window.HasTransparentSurface);
        hub.Publish(current with { State = PlaybackStatus.Stopped });
        await Task.Delay(80);
        verify("desktop_lyrics_stop_clears_line", window.DisplayedLyrics == "尚未播放");
        window.Dispose();
        hub.Publish(current);
        await Task.Delay(80);
        verify("desktop_lyrics_dispose_unsubscribes", !window.IsLyricsVisible);
    }
}
#endif
