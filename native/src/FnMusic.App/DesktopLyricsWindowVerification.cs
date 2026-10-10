#if DEBUG
using FnMusic.Core;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;

namespace FnMusic.App;

/// <summary>Invoked by the consolidated window regression; does not control the real player.</summary>
internal static class DesktopLyricsWindowVerification
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

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
        window.ApplyOptions(window.Options with { Locked = false, Theme = "light", X = int.MinValue, Y = int.MinValue });
        window.RecoverPosition();
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var position = window.AppWindow.Position; var size = window.AppWindow.Size;
        verify("desktop_lyrics_unlock_and_recover", presenter.HasTitleBar && presenter.IsResizable
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
