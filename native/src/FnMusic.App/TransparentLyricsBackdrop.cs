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

    internal void SuppressNativeFrame() => SuppressNativeFrame(window);

    private static void SuppressNativeFrame(nint window)
    {
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
            if (!SetWindowSubclass(window, procedure, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            attached = true;
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
        // An empty blur region enables alpha composition without blurring the desktop behind the lyrics.
        var region = CreateRectRgn(0, 0, 0, 0);
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
        if (message == 0x0014 && ClearBackground(window, (nint)wParam)) return 1; // WM_ERASEBKGND, BLACK_BRUSH gives zero-alpha pixels.
        if (message == 0x031E) // WM_DWMCOMPOSITIONCHANGED
        {
            try { ConfigureComposition(window); }
            catch (Exception) { /* Never propagate an initialization failure across a native callback. */ }
        }
        return DefSubclassProc(window, message, wParam, lParam);
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
}
