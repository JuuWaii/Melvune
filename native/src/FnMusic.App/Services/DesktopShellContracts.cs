namespace FnMusic.App.Services;

internal enum DesktopShellCommand
{
    ShowMain, PlayPause, Previous, Next, ToggleDesktopLyrics, UnlockDesktopLyrics, Exit
}

internal sealed record DesktopShellState(bool IsPlaying, bool CanControl, bool LyricsEnabled, bool LyricsLocked);
