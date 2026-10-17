using System.Runtime.InteropServices;

namespace Melvune.App.Services;

/// <summary>Optional, window-bound hotkeys. Construct, enable and dispose on the window's UI thread.</summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    internal const uint HotkeyMessage = 0x0312;
    internal const uint DestroyMessage = 0x0082;
    internal const nuint SubclassId = 0x464E484B;
    internal const uint Modifiers = 0x4003; // MOD_CONTROL | MOD_ALT | MOD_NOREPEAT
    internal readonly record struct Binding(int Id, uint Key, string Label, DesktopShellCommand Command);
    internal static readonly Binding[] Bindings =
    [
        new(0x5101, 0x20, "Ctrl+Alt+Space：播放 / 暂停", DesktopShellCommand.PlayPause),
        new(0x5102, 0x25, "Ctrl+Alt+Left：上一首", DesktopShellCommand.Previous),
        new(0x5103, 0x27, "Ctrl+Alt+Right：下一首", DesktopShellCommand.Next),
        new(0x5104, 0x4C, "Ctrl+Alt+L：桌面歌词", DesktopShellCommand.ToggleDesktopLyrics),
        new(0x5105, 0x55, "Ctrl+Alt+U：解锁并找回歌词", DesktopShellCommand.UnlockDesktopLyrics)
    ];

    private readonly nint owner;
    private readonly Action<DesktopShellCommand> callback;
    private readonly IHotkeyNative native;
    private readonly SubclassProcedure procedure;
    private readonly HashSet<int> registered = [];
    private readonly List<string> conflicts = [];
    private bool attached;
    private bool enabled;
    private bool disposed;

    public IReadOnlyList<string> Conflicts => conflicts.AsReadOnly();
    public int RegisteredCount => registered.Count;

    public GlobalHotkeyService(nint owner, Action<DesktopShellCommand> callback)
        : this(owner, callback, new WindowsHotkeyNative()) { }

    internal GlobalHotkeyService(nint owner, Action<DesktopShellCommand> callback, IHotkeyNative native)
    {
        if (owner == 0) throw new ArgumentException("A window handle is required.", nameof(owner));
        ArgumentNullException.ThrowIfNull(callback);
        this.owner = owner;
        this.callback = callback;
        this.native = native;
        if (!native.IsOwnerThread(owner)) throw new InvalidOperationException("Hotkeys require the window's UI thread.");
        procedure = WindowProcedure;
    }

    public void SetEnabled(bool value)
    {
        if (disposed) return;
        if (!native.IsOwnerThread(owner)) throw new InvalidOperationException("Hotkeys require the window's UI thread.");
        if (value == enabled) return;
        if (!value)
        {
            enabled = false;
            ReleaseRegistrations();
            conflicts.Clear();
            Detach();
            return;
        }

        conflicts.Clear();
        if (!attached && !native.Attach(owner, procedure, SubclassId))
        {
            conflicts.Add("无法启用全局快捷键，请关闭后重新开启。");
            return;
        }
        attached = true;
        enabled = true;
        foreach (var binding in Bindings)
        {
            if (native.Register(owner, binding.Id, Modifiers, binding.Key)) registered.Add(binding.Id);
            else conflicts.Add($"{binding.Label} 未能注册，可能被其他应用占用。");
        }
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Native callbacks must never propagate managed exceptions into the message pump.
        try
        {
            if (message == HotkeyMessage && TryDispatch(wParam, lParam)) return 0;
            if (message == DestroyMessage) Dispose();
        }
        catch { }
        return native.Forward(window, message, wParam, lParam);
    }

    private bool TryDispatch(nuint id, nint keyData)
    {
        if (!enabled || disposed || id > int.MaxValue || !registered.Contains((int)id)) return false;
        var binding = Array.Find(Bindings, b => b.Id == (int)id);
        // Ignore unrelated or malformed messages, including another service's hotkey identifiers.
        uint packed = unchecked((uint)keyData.ToInt64());
        if ((packed & 0xFFFF & ~0x4000u) != 3 || (packed >> 16) != binding.Key) return false;
        try { callback(binding.Command); }
        catch { /* A failed player action must not break subsequent hotkeys. */ }
        return true;
    }

    private void ReleaseRegistrations()
    {
        foreach (int id in registered) native.Unregister(owner, id);
        registered.Clear();
    }

    private void Detach()
    {
        if (!attached) return;
        native.Detach(owner, procedure, SubclassId);
        attached = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        if (!native.IsOwnerThread(owner)) throw new InvalidOperationException("Hotkeys require the window's UI thread.");
        enabled = false;
        ReleaseRegistrations();
        Detach();
        conflicts.Clear();
        disposed = true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    internal interface IHotkeyNative
    {
        bool IsOwnerThread(nint owner);
        bool Attach(nint owner, SubclassProcedure procedure, nuint id);
        void Detach(nint owner, SubclassProcedure procedure, nuint id);
        bool Register(nint owner, int id, uint modifiers, uint key);
        bool Unregister(nint owner, int id);
        nint Forward(nint owner, uint message, nuint wParam, nint lParam);
    }

    private sealed class WindowsHotkeyNative : IHotkeyNative
    {
        public bool IsOwnerThread(nint owner) => GetWindowThreadProcessId(owner, out _) == GetCurrentThreadId();
        public bool Attach(nint owner, SubclassProcedure procedure, nuint id) => SetWindowSubclass(owner, procedure, id, 0);
        public void Detach(nint owner, SubclassProcedure procedure, nuint id) => RemoveWindowSubclass(owner, procedure, id);
        public bool Register(nint owner, int id, uint modifiers, uint key) => RegisterHotKey(owner, id, modifiers, key);
        public bool Unregister(nint owner, int id) => UnregisterHotKey(owner, id);
        public nint Forward(nint owner, uint message, nuint wParam, nint lParam) => DefSubclassProc(owner, message, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}
