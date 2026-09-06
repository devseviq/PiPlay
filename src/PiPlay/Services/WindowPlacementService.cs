using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PiPlay.Models;

namespace PiPlay.Services;

/// <summary>
/// Saves/restores native window placement and clamps a window to a visible monitor work
/// area when the monitor layout changes (spec 16.4, REQ-WINDOW-01, REQ-PROFILE-02).
/// Uses Win32 placement APIs directly (pixel coordinates) so it is correct under
/// PerMonitor V2 DPI without any extra dependency. PiPlay never restores a window
/// fully off-screen.
/// <para>
/// Coordinate spaces (PP-07): <c>WINDOWPLACEMENT.rcNormalPosition</c> is workspace-relative for
/// ordinary top-level windows and screen-relative for <c>WS_EX_TOOLWINDOW</c> windows. Persisted
/// <see cref="PlacementData"/>, monitor lookup, and clamping all use virtual-screen pixels; the
/// conversion happens here, at the Win32 boundary, via <see cref="PlacementMath"/>.
/// </para>
/// </summary>
public static class WindowPlacementService
{
    /// <summary>Capture the window's normal-position bounds (screen pixels) and the monitor it lives on.</summary>
    public static PlacementData? TryCapture(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return null;

            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref wp)) return null;

            var raw = wp.rcNormalPosition;
            var screen = PlacementMath.WorkspaceToScreen(
                ToRectI(raw), GetPrimaryWorkArea(), IsToolWindow(hwnd));
            var data = new PlacementData
            {
                X = screen.Left,
                Y = screen.Top,
                Width = screen.Width,
                Height = screen.Height,
                Maximized = wp.showCmd == SW_SHOWMAXIMIZED,
                DpiScale = GetDpiScaleSafe(window),
                CoordinateSpace = PlacementData.ScreenCoordinateSpace,
            };

            var r = ToRect(screen);
            var monitor = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
            if (TryGetMonitorInfo(monitor, out var mi))
            {
                data.MonitorDeviceName = mi.szDevice;
                data.MonitorWorkArea = ToRectData(mi.rcWork);
            }
            return data;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to capture window placement.", ex);
            return null;
        }
    }

    /// <summary>Restore placement, clamped to a currently-visible monitor work area.</summary>
    public static void Restore(Window window, PlacementData? data)
    {
        if (data is null || data.Width <= 0 || data.Height <= 0) return;
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();

            // Monitor lookup and clamping happen in screen space; legacy unmarked data (a raw
            // rcNormalPosition capture) is lifted into screen space first.
            var toolWindow = IsToolWindow(hwnd);
            var primaryWork = GetPrimaryWorkArea();
            var target = PlacementMath.ToScreenRect(data, primaryWork, toolWindow);
            var work = ResolveWorkArea(data, target);
            var clamped = PlacementMath.Clamp(target, ToRectI(work));

            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(hwnd, ref wp); // seed with current values
            wp.rcNormalPosition = ToRect(PlacementMath.ScreenToWorkspace(clamped, primaryWork, toolWindow));
            wp.showCmd = data.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL;
            wp.flags = 0;
            SetWindowPlacement(hwnd, ref wp);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restore window placement.", ex);
        }
    }

    /// <param name="data">Saved placement (monitor identity and fallback work area).</param>
    /// <param name="screenTarget">The saved bounds already converted to screen pixels.</param>
    private static RECT ResolveWorkArea(PlacementData data, RectI screenTarget)
    {
        var monitors = EnumerateMonitors();

        // Prefer the saved monitor if it is still present (REQ-PROFILE-02).
        if (!string.IsNullOrEmpty(data.MonitorDeviceName))
        {
            foreach (var m in monitors)
                if (string.Equals(m.szDevice, data.MonitorDeviceName, StringComparison.Ordinal))
                    return m.rcWork;
        }

        // Otherwise the nearest monitor to the saved rectangle (MonitorFromRect takes screen pixels).
        var target = ToRect(screenTarget);
        var hmon = MonitorFromRect(ref target, MONITOR_DEFAULTTONEAREST);
        if (TryGetMonitorInfo(hmon, out var mi)) return mi.rcWork;

        if (data.MonitorWorkArea is { } w)
            return new RECT { Left = w.X, Top = w.Y, Right = w.X + w.Width, Bottom = w.Y + w.Height };

        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /// <summary>Primary monitor work area in screen pixels; its origin is the workspace offset.</summary>
    private static RectI GetPrimaryWorkArea()
    {
        var work = new RECT();
        // On failure the offset is (0,0): the window then round-trips exactly as before PP-07.
        return SystemParametersInfoW(SPI_GETWORKAREA, 0, ref work, 0)
            ? ToRectI(work)
            : new RectI(0, 0, 0, 0);
    }

    private static bool IsToolWindow(IntPtr hwnd) =>
        (GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0;

    private static RectI ToRectI(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private static RECT ToRect(RectI r) =>
        new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };

    private static List<MONITORINFOEX> EnumerateMonitors()
    {
        var list = new List<MONITORINFOEX>();
        bool Callback(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data)
        {
            if (TryGetMonitorInfo(hMon, out var mi)) list.Add(mi);
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return list;
    }

    private static bool TryGetMonitorInfo(IntPtr monitor, out MONITORINFOEX info)
    {
        info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    private static RectData ToRectData(RECT r) => new()
    {
        X = r.Left,
        Y = r.Top,
        Width = r.Right - r.Left,
        Height = r.Bottom - r.Top,
    };

    private static double GetDpiScaleSafe(Window window)
    {
        try { return VisualTreeHelper.GetDpi(window).DpiScaleX; }
        catch { return 1.0; }
    }

    // --- Win32 interop ---

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMAXIMIZED = 3;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const uint SPI_GETWORKAREA = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
}
