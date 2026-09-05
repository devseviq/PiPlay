using System.Runtime.InteropServices;

namespace PiPlay.Services;

/// <summary>
/// Applies a device-pixel rounded region to the top-level Popout HWND. Unlike a WPF Clip, the native
/// window region also clips child HWND content such as the standard WebView2 control.
/// </summary>
internal static class RoundedWindowRegionApplier
{
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmExtendedFrameBounds = 9;
    private const int RegionError = 0;

    public static bool Apply(IntPtr hwnd, double radiusDip)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return false;

        var geometry = RoundedWindowRegionPolicy.CreateGeometry(
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            radiusDip,
            GetDpiForWindow(hwnd));
        if (geometry is null) return false;

        var g = geometry.Value;
        // CreateRoundRectRgn excludes the window's final right/bottom straight-edge pixel unless
        // the exclusive endpoints run one past the bounds (verified natively with PtInRegion);
        // int.MaxValue dimensions cannot come from GetWindowRect but would overflow the +1.
        if (g.WidthPx == int.MaxValue || g.HeightPx == int.MaxValue) return false;
        var region = CreateRoundRectRgn(
            0,
            0,
            g.WidthPx + 1,
            g.HeightPx + 1,
            g.EllipseWidthPx,
            g.EllipseHeightPx);
        if (region == IntPtr.Zero) return false;

        // After success Windows owns the HRGN and eventually deletes it. On failure ownership stays
        // here, so release it explicitly (SetWindowRgn contract).
        if (SetWindowRgn(hwnd, region, redraw: true) != 0) return true;
        _ = DeleteObject(region);
        return false;
    }

    public static bool Clear(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && SetWindowRgn(hwnd, IntPtr.Zero, redraw: true) != 0;

    public static bool IsSnapLike(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var windowRect)) return false;
        // GetWindowRect can include DPI-virtualized invisible resize borders. Snap classification
        // needs the visible physical frame that Windows aligns to rcWork; fall back for older DWM.
        var visibleRect = windowRect;
        if (DwmGetWindowAttribute(
                hwnd,
                DwmExtendedFrameBounds,
                out var extendedFrame,
                Marshal.SizeOf<RECT>()) >= 0 &&
            extendedFrame.Right > extendedFrame.Left &&
            extendedFrame.Bottom > extendedFrame.Top)
        {
            visibleRect = extendedFrame;
        }
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        return RoundedWindowRegionPolicy.IsSnapLike(
            visibleRect.Left,
            visibleRect.Top,
            visibleRect.Right,
            visibleRect.Bottom,
            info.Work.Left,
            info.Work.Top,
            info.Work.Right,
            info.Work.Bottom);
    }

    internal static bool HasCustomRegionForTests(IntPtr hwnd)
    {
        var probe = CreateRectRgn(0, 0, 0, 0);
        if (probe == IntPtr.Zero) return false;
        try { return GetWindowRgn(hwnd, probe) != RegionError; }
        finally { _ = DeleteObject(probe); }
    }

    internal static bool IsPointVisibleForTests(IntPtr hwnd, int x, int y)
    {
        var probe = CreateRectRgn(0, 0, 0, 0);
        if (probe == IntPtr.Zero) return false;
        try
        {
            if (GetWindowRgn(hwnd, probe) == RegionError) return true; // rectangular/default window
            return PtInRegion(probe, x, y);
        }
        finally { _ = DeleteObject(probe); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool PtInRegion(IntPtr region, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
}
