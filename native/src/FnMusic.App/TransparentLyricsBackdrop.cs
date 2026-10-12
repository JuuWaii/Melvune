using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FnMusic.App;

/// <summary>
/// Supplies a zero-alpha system composition surface, not a transparent XAML brush on an opaque HWND.
/// The native background must also retain zero alpha when DWM repaints it.
/// </summary>
internal sealed partial class TransparentLyricsBackdrop(nint window) : SystemBackdrop, IDisposable
{
    private readonly SubclassProcedure procedure = Dispatch;
    private global::Windows.UI.Composition.Compositor? compositor;
    private global::Windows.UI.Composition.CompositionColorBrush? brush;
    private bool attached;
    internal bool IsConfigured { get; private set; }
    internal bool IsInputPassThrough => (GetWindowLongPtrW(window, -20).ToInt64() & 0x80020) == 0x80020;

    // Strip the native resize strip before attaching XAML. Alpha is provided by the backdrop;
    // NOREDIRECTIONBITMAP cannot be enabled on this already-created WinUI HWND (error 87).
    internal void PrepareNativeHost()
    {
        AttachNativeMessages();
        SuppressNativeFrame(window);
    }

    internal void SetInputPassThrough(bool value)
    {
        long previous = GetWindowLongPtrW(window, -20).ToInt64();
        const long inputStyles = 0x80020 | 0x08000000; // WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
        long updated = value ? previous | inputStyles : previous & ~inputStyles;
        SetStyle(window, -20, updated);
        try
        {
            if (value)
            {
                // LWA_ALPHA=255 keeps the lyric pixels visible. No color key and no whole-window fade.
                // Layered+transparent supplies native cross-process mouse passthrough, unlike HTTRANSPARENT alone.
                if (!SetLayeredWindowAttributes(window, 0, 255, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            else ConfigureComposition(window);
            SuppressNativeFrame(window);
        }
        catch { SetStyle(window, -20, previous); throw; }
    }

    private void AttachNativeMessages()
    {
        if (attached) return;
        if (!SetWindowSubclass(window, procedure, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        attached = true;
    }

    internal void SuppressNativeFrame() => SuppressNativeFrame(window);

    private static void SuppressNativeFrame(nint window)
    {
        // Presenter flags alone leave a resize frame in recent Windows App SDK versions.
        // This overlay uses pointer dragging and the width menu rather than a native resize frame.
        long style = GetWindowLongPtrW(window, -16).ToInt64();
        long popup = (style & ~(0x00C00000L | 0x00040000L | 0x00080000L | 0x00030000L)) | 0x80000000L;
        SetStyle(window, -16, popup);
        if (!SetWindowPos(window, 0, 0, 0, 0, 0, 0x0037)) // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
            throw new Win32Exception(Marshal.GetLastWin32Error());
        uint disabledNonClient = 1; // DWMNCRP_DISABLED: suppress cached caption/frame/shadow rendering.
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 2, ref disabledNonClient, sizeof(uint)));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        // DWM draws a one-pixel outline even when the presenter has neither a border nor a title bar.
        // These documented attributes suppress it without disabling the alpha-composition path.
        uint noBorder = 0xFFFFFFFE, doNotRound = 1;
        _ = DwmSetWindowAttribute(window, 34, ref noBorder, sizeof(uint)); // DWMWA_BORDER_COLOR / COLOR_NONE
        _ = DwmSetWindowAttribute(window, 33, ref doNotRound, sizeof(uint)); // DWMWA_WINDOW_CORNER_PREFERENCE / DONOTROUND
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        try
        {
            AttachNativeMessages();
            ConfigureComposition(window);
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
            compositor = new global::Windows.UI.Composition.Compositor();
            brush = compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
            target.SystemBackdrop = brush;
            var dc = GetDC(window);
            if (dc != 0) { try { ClearBackground(window, dc); } finally { ReleaseDC(window, dc); } }
            IsConfigured = true;
        }
        catch { target.SystemBackdrop = null; Dispose(); throw; }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        Dispose();
        base.OnTargetDisconnected(target);
    }

    private static void ConfigureComposition(nint window)
    {
        var margins = new Margins();
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(window, ref margins));
        SuppressNativeFrame(window);
        // An offscreen blur region enables alpha composition without blurring the desktop behind the lyrics.
        // Keeping it nonempty avoids NULLREGION being treated as a whole-window material.
        var region = CreateRectRgn(-2, -2, -1, -1);
        if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var blur = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(window, ref blur));
        }
        finally { DeleteObject(region); }
    }

    private static bool ClearBackground(nint window, nint dc) =>
        GetClientRect(window, out var rectangle) && FillRect(dc, ref rectangle, GetStockObject(4)) != 0;

    private static nint Dispatch(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0083) return 0; // WM_NCCALCSIZE: full window is client area, including the former top resize strip.
        if (message == 0x0085) return 0; // WM_NCPAINT: there is no caption, frame or rectangle shadow to paint.
        if (message == 0x0086) return 1; // WM_NCACTIVATE: DefWindowProc otherwise paints a classic caption despite a zero-sized NC area.
        if (message == 0x0084 && (GetWindowLongPtrW(window, -20).ToInt64() & 0x20) != 0) return -1; // Same-thread fallback; cross-process uses layered styles.
        if (message == 0x0014 && ClearBackground(window, (nint)wParam)) return 1; // WM_ERASEBKGND, BLACK_BRUSH gives zero-alpha pixels.
        if (message == 0x031E) // WM_DWMCOMPOSITIONCHANGED
        {
            try
            {
                if ((GetWindowLongPtrW(window, -20).ToInt64() & 0x80000) == 0) ConfigureComposition(window);
                else SuppressNativeFrame(window);
            }
            catch (Exception) { /* Never propagate an initialization failure across a native callback. */ }
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private static void SetStyle(nint window, int index, long value)
    {
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtrW(window, index, (nint)value) == 0 && Marshal.GetLastWin32Error() is int error && error != 0)
        {
            var exception = new Win32Exception(error);
            exception.Data["StyleIndex"] = index;
            exception.Data["WindowStyle"] = unchecked((uint)value);
            throw exception;
        }
    }

    public void Dispose()
    {
        IsConfigured = false;
        if (attached) { RemoveWindowSubclass(window, procedure, 1); attached = false; }
        brush?.Dispose(); brush = null; compositor?.Dispose(); compositor = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind { public uint Flags; public int Enable; public nint Region; public int Transition; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint window, ref BlurBehind blur);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref uint value, uint size);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int value);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeRect rectangle, nint brush);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
}
