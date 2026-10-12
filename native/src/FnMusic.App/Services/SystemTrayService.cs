using System.Runtime.InteropServices;
using System.Text;

namespace FnMusic.App.Services;

/// <summary>
/// Owns an HWND notification-area icon. Create, update and dispose on the HWND's UI thread.
/// The service never hides the owner; callers must check IsAvailable before hiding it.
/// </summary>
internal sealed class SystemTrayService : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 0x413;
    private const nuint SubclassId = 0x464E5452; // FNTR; independent of the lyrics subclass.
    private const uint WmNcDestroy = 0x82, WmContextMenu = 0x7B, WmLeftDoubleClick = 0x203;
    private const uint WmRightButtonUp = 0x205, NinKeySelect = 0x401;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifShowTip = 0x80;
    private const uint MfDisabled = 3, MfChecked = 8, MfSeparator = 0x800;
    private readonly nint owner;
    private readonly string iconPath;
    private readonly Action<DesktopShellCommand> callback;
    private readonly SubclassProcedure procedure;
    private readonly uint ownerThread;
    private uint taskbarCreatedMessage;
    private nint icon;
    private bool ownsIcon, attached, version4, disposed, showingMenu;
#if DEBUG
    private bool menuPreview;
#endif
    private DesktopShellState state = new(false, false, false, false);

    internal SystemTrayService(nint owner, string iconPath, Action<DesktopShellCommand> callback)
    {
        this.owner = owner;
        this.iconPath = iconPath;
        this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
        procedure = Dispatch;
        ownerThread = GetWindowThreadProcessId(owner, out _);
        if (owner == 0 || ownerThread != GetCurrentThreadId()) return;
        try
        {
            taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
            if (taskbarCreatedMessage == 0 || !SetWindowSubclass(owner, procedure, SubclassId, 0)) return;
            attached = true;
            AddIcon();
        }
        catch
        {
            Dispose();
            // An unavailable notification area must not prevent the ordinary app window opening.
        }
    }

    internal bool IsAvailable { get; private set; }
    internal event Action<bool>? AvailabilityChanged;

    internal void Update(DesktopShellState value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureOwnerThread();
        if (disposed) return;
        if (state == value && IsAvailable) return;
        state = value;
        if (!attached) return;
        if (!IsAvailable) { AddIcon(); return; }
        NotifyIconData data = CreateData();
        if (!Shell_NotifyIconW(1, ref data))
        {
            SetAvailable(false);
            AddIcon();
        }
    }

    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = owner, Id = IconId,
        Flags = NifMessage | NifIcon | NifTip | NifShowTip, Callback = CallbackMessage, Icon = icon,
        Tip = "飞牛音乐" + (state.IsPlaying ? " · 正在播放" : ""), Info = "", InfoTitle = "",
        State = 0, StateMask = 0, Version = 0, InfoFlags = 0, Guid = Guid.Empty, BalloonIcon = 0
    };

    private void AddIcon()
    {
        if (disposed || !attached || !IsWindow(owner)) return;
        ReplaceIcon();
        if (icon == 0) { SetAvailable(false); RestoreOwnerIfHidden(); return; }
        NotifyIconData data = CreateData();
        // TaskbarCreated also occurs on primary-monitor DPI changes, when an existing icon may remain.
        bool added = Shell_NotifyIconW(0, ref data) || Shell_NotifyIconW(1, ref data);
        if (added)
        {
            data.Version = 4;
            version4 = Shell_NotifyIconW(4, ref data);
        }
        SetAvailable(added);
        if (!added) RestoreOwnerIfHidden();
    }

    private void ReplaceIcon()
    {
        uint dpi = GetDpiForWindow(owner);
        nint replacement = LoadImageW(0, iconPath, 1,
            GetSystemMetricsForDpi(49, dpi == 0 ? 96 : dpi),
            GetSystemMetricsForDpi(50, dpi == 0 ? 96 : dpi), 0x10); // LR_LOADFROMFILE; owned handle.
        bool replacementOwned = replacement != 0;
        if (replacement == 0) replacement = LoadIconW(0, (nint)32512); // Shared application fallback.
        nint previous = icon;
        bool previousOwned = ownsIcon;
        icon = replacement;
        ownsIcon = replacementOwned;
        if (previousOwned && previous != 0) _ = DestroyIcon(previous);
    }

    private void SetAvailable(bool value)
    {
        if (IsAvailable == value) return;
        IsAvailable = value;
        try { AvailabilityChanged?.Invoke(value); }
        catch { RestoreOwnerIfHidden(); }
    }

    private nint Dispatch(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (message == WmNcDestroy)
            {
                Dispose();
                return DefSubclassProc(window, message, wParam, lParam);
            }
            if (!disposed && message == taskbarCreatedMessage)
            {
                AddIcon();
                return 0;
            }
            if (!disposed && message == CallbackMessage)
            {
                ulong packed = unchecked((ulong)lParam.ToInt64());
                uint notification = version4 ? (uint)(packed & 0xFFFF) : (uint)packed;
                uint receivedId = version4 ? (uint)((packed >> 16) & 0xFFFF) : (uint)wParam;
                if (receivedId != IconId) return 0;
                if (notification is WmLeftDoubleClick or NinKeySelect) InvokeCommand(DesktopShellCommand.ShowMain);
                else if (notification == WmContextMenu || (!version4 && notification == WmRightButtonUp)) ShowMenu();
                return 0;
            }
        }
        catch
        {
            // Managed exceptions must never escape a reverse P/Invoke native window procedure.
            RestoreOwnerIfHidden();
            if (message == CallbackMessage) return 0;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        if (showingMenu || disposed || !IsAvailable) return;
        showingMenu = true;
        nint menu = 0;
        try
        {
            menu = BuildMenu();
            if (menu == 0) return;
            Point anchor;
            NotifyIconIdentifier identifier = new()
            {
                Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = owner, Id = IconId, Guid = Guid.Empty
            };
            // WM_CONTEXTMENU's packed wParam is undefined under VERSION_4; use the icon's actual rect.
            if (Shell_NotifyIconGetRect(ref identifier, out Rect bounds) == 0)
                anchor = new Point { X = (bounds.Left + bounds.Right) / 2, Y = bounds.Top };
            else if (!GetCursorPos(out anchor)) return;
#if DEBUG
            // The test driver cannot select Explorer's notification-area surface. Keep the real
            // native menu within its own window's capture bounds; production anchors at the icon.
            if (menuPreview && GetWindowRect(owner, out Rect previewBounds))
                anchor = new Point { X = previewBounds.Left + 300, Y = previewBounds.Top + 220 };
#endif
            _ = SetForegroundWindow(owner);
            uint alignment = GetSystemMetrics(40) != 0 ? 8u : 0; // SM_MENUDROPALIGNMENT / TPM_RIGHTALIGN.
            uint selected = TrackPopupMenu(menu, 0x100 | 0x80 | 2 | alignment,
                anchor.X, anchor.Y, 0, owner, 0);
            _ = PostMessageW(owner, 0, 0, 0); // WM_NULL, required for repeat notification-area menus.
            if (!disposed)
            {
                NotifyIconData data = CreateData();
                _ = Shell_NotifyIconW(3, ref data); // Return focus to the notification area.
                if (selected >= 100 && selected <= 100 + (uint)DesktopShellCommand.Exit)
                    InvokeCommand((DesktopShellCommand)(selected - 100));
            }
        }
        finally
        {
            if (menu != 0) _ = DestroyMenu(menu);
            showingMenu = false;
        }
    }

    private nint BuildMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return 0;
        try
        {
            Add(DesktopShellCommand.ShowMain, "打开飞牛音乐");
            Separator();
            Add(DesktopShellCommand.PlayPause, state.IsPlaying ? "暂停" : "播放", state.CanControl);
            Add(DesktopShellCommand.Previous, "上一首", state.CanControl);
            Add(DesktopShellCommand.Next, "下一首", state.CanControl);
            Separator();
            Add(DesktopShellCommand.ToggleDesktopLyrics, state.LyricsEnabled ? "隐藏桌面歌词" : "显示桌面歌词",
                check: state.LyricsEnabled);
            Add(DesktopShellCommand.UnlockDesktopLyrics, "解锁并找回桌面歌词", state.LyricsEnabled && state.LyricsLocked);
            Separator();
            Add(DesktopShellCommand.Exit, "退出飞牛音乐");
            return menu;

            void Add(DesktopShellCommand command, string label, bool enabled = true, bool check = false)
            {
                if (!AppendMenuW(menu, (enabled ? 0 : MfDisabled) | (check ? MfChecked : 0),
                    100 + (nuint)command, label)) throw new InvalidOperationException("Unable to create notification-area menu.");
            }
            void Separator()
            {
                if (!AppendMenuW(menu, MfSeparator, 0, null)) throw new InvalidOperationException("Unable to create notification-area menu.");
            }
        }
        catch { _ = DestroyMenu(menu); throw; }
    }

    private void InvokeCommand(DesktopShellCommand command)
    {
        if (disposed) return;
        if (command is DesktopShellCommand.PlayPause or DesktopShellCommand.Previous or DesktopShellCommand.Next
            && !state.CanControl) return;
        if (command == DesktopShellCommand.UnlockDesktopLyrics && !(state.LyricsEnabled && state.LyricsLocked)) return;
        try { callback(command); }
        catch { RestoreOwnerIfHidden(); }
    }

    private void RestoreOwnerIfHidden()
    {
        if (disposed || !IsWindow(owner) || IsWindowVisible(owner)) return;
        try
        {
            _ = ShowWindow(owner, 5); // SW_SHOW, preserve the ordinary window as the recovery path.
            callback(DesktopShellCommand.ShowMain);
        }
        catch { /* The HWND is already visible even when managed command dispatch fails. */ }
    }

    private void EnsureOwnerThread()
    {
        if (ownerThread != 0 && ownerThread != GetCurrentThreadId())
            throw new InvalidOperationException("Notification-area service must run on the owner window thread.");
    }

    public void Dispose()
    {
        EnsureOwnerThread();
        if (disposed) return;
        disposed = true;
        NotifyIconData data = CreateData();
        if (attached)
        {
            _ = Shell_NotifyIconW(2, ref data);
            _ = RemoveWindowSubclass(owner, procedure, SubclassId);
            attached = false;
        }
        SetAvailable(false);
        if (ownsIcon && icon != 0) _ = DestroyIcon(icon);
        icon = 0;
        ownsIcon = false;
    }

#if DEBUG
    internal void OpenNativeMenuForVerification()
    {
        menuPreview = true;
        try { ShowMenu(); } finally { menuPreview = false; }
    }
    // Read back real native menu state; the root regression can verify labels, flags and safe actions
    // without injecting clicks into the user's Explorer or restarting it.
    internal IReadOnlyList<(DesktopShellCommand Command, string Text, bool Enabled, bool Checked)> ReadNativeMenuForVerification()
    {
        EnsureOwnerThread();
        nint menu = BuildMenu();
        if (menu == 0) throw new InvalidOperationException("Notification-area menu unavailable.");
        try
        {
            List<(DesktopShellCommand, string, bool, bool)> result = [];
            foreach (DesktopShellCommand command in Enum.GetValues<DesktopShellCommand>())
            {
                uint id = 100 + (uint)command;
                StringBuilder text = new(128);
                _ = GetMenuStringW(menu, id, text, text.Capacity, 0);
                uint flags = GetMenuState(menu, id, 0);
                result.Add((command, text.ToString(), flags != uint.MaxValue && (flags & MfDisabled) == 0,
                    flags != uint.MaxValue && (flags & MfChecked) != 0));
            }
            return result;
        }
        finally { _ = DestroyMenu(menu); }
    }

    internal void VerifyTaskbarRecreation()
    {
        EnsureOwnerThread();
        if (!disposed && attached) _ = SendMessageW(owner, taskbarCreatedMessage, 0, 0);
    }
#endif

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier
    { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rect);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RegisterWindowMessageW(string value);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint LoadImageW(nint instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint LoadIconW(nint instance, nint name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint value);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
#if DEBUG
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetMenuStringW(nint menu, uint item, StringBuilder text, int count, uint flags);
    [DllImport("user32.dll")] private static extern uint GetMenuState(nint menu, uint id, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);
#endif
}
