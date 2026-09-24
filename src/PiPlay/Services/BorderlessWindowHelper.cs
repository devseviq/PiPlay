using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PiPlay.Services;

/// <summary>
/// Native-window helpers for borderless (WindowStyle=None) PiPlay windows: work-area maximize
/// via WM_GETMINMAXINFO and larger resize hit zones via WM_NCHITTEST (Q-7 / REQ-WINDOW-02).
/// </summary>
public static class BorderlessWindowHelper
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCDESTROY = 0x0082;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int SC_MOVE = 0xF010;
    private const int HTCAPTION = 2;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int SM_SWAPBUTTON = 23;
    private const uint DefaultDpi = 96;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly UIntPtr ResizeSubclassId = new(0x5049504C); // "PIPL"
    private static readonly Dictionary<IntPtr, ResizeSubclassState> ResizeSubclassStates = new();

    public static void EnableProperMaximize(Window window)
    {
        void Hook()
        {
            if (PresentationSource.FromVisual(window) is HwndSource src)
            {
                src.AddHook((
                    IntPtr hwnd,
                    int msg,
                    IntPtr wParam,
                    IntPtr lParam,
                    ref bool handled) => WndProc(window, hwnd, msg, wParam, lParam, ref handled));
            }
        }

        if (PresentationSource.FromVisual(window) is not null)
            Hook();
        else
            window.SourceInitialized += (_, _) => Hook();
    }

    public static void EnableExpandedResizeZones(Window window, Action<bool>? moveSizeStateChanged = null)
    {
        void Hook()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero && PresentationSource.FromVisual(window) is HwndSource src)
                hwnd = src.Handle;
            InstallResizeSubclass(hwnd, window, moveSizeStateChanged);
        }

        if (PresentationSource.FromVisual(window) is not null)
            Hook();
        else
            window.SourceInitialized += (_, _) => Hook();
    }

    /// <summary>
    /// Queue a validated player-surface gesture into the native caption move loop. Posting
    /// WM_SYSCOMMAND lets the WebView2 callback unwind before Windows enters its modal move loop.
    /// </summary>
    internal static bool TryBeginWindowMove(Window window)
    {
        if (window.WindowState != WindowState.Normal || !IsPrimaryButtonDown()) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var point)) return false;

        return QueueWindowMove(hwnd, point.X, point.Y, PostMessage);
    }

    /// <summary>
    /// The page script arms a drag on the LOGICAL primary button (<c>event.button === 0</c>), but
    /// GetAsyncKeyState reads PHYSICAL buttons; with swapped buttons the primary is the right one.
    /// </summary>
    internal static bool IsPrimaryButtonDown() =>
        (GetAsyncKeyState(PrimaryButtonVirtualKey(GetSystemMetrics(SM_SWAPBUTTON) != 0)) & 0x8000) != 0;

    internal static int PrimaryButtonVirtualKey(bool buttonsSwapped) => buttonsSwapped ? VK_RBUTTON : VK_LBUTTON;

    internal static IntPtr PackScreenPointForTests(int x, int y) => PackScreenPoint(x, y);

    internal static bool QueueWindowMoveForTests(
        IntPtr hwnd,
        int x,
        int y,
        Func<IntPtr, int, IntPtr, IntPtr, bool> postMessage) =>
        QueueWindowMove(hwnd, x, y, postMessage);

    private static bool QueueWindowMove(
        IntPtr hwnd,
        int x,
        int y,
        Func<IntPtr, int, IntPtr, IntPtr, bool> postMessage) =>
        hwnd != IntPtr.Zero && postMessage(
            hwnd,
            WM_SYSCOMMAND,
            new IntPtr(SC_MOVE | HTCAPTION),
            PackScreenPoint(x, y));

    private static IntPtr PackScreenPoint(int x, int y)
    {
        var packed = unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16)));
        return new IntPtr(packed);
    }

    private static IntPtr WndProc(
        Window window,
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var work = info.rcWork;
        var area = info.rcMonitor;
        var maxBounds = CalculateMaximizedBounds(area, work);
        mmi.ptMaxPosition = maxBounds.Position;
        mmi.ptMaxSize = maxBounds.Size;
        mmi.ptMinTrackSize = CalculateMinimumTrackSize(
            mmi.ptMinTrackSize,
            window.MinWidth,
            window.MinHeight,
            GetDpiForWindow(hwnd));
        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: true);

        handled = true;
        return IntPtr.Zero;
    }

    private static (POINT Position, POINT Size) CalculateMaximizedBounds(RECT monitor, RECT work) =>
        (
            new POINT
            {
                X = work.Left - monitor.Left,
                Y = work.Top - monitor.Top,
            },
            new POINT
            {
                X = work.Right - work.Left,
                Y = work.Bottom - work.Top,
            });

    private static POINT CalculateMinimumTrackSize(
        POINT existing,
        double minWidthDips,
        double minHeightDips,
        uint dpi)
    {
        var effectiveDpi = dpi == 0 ? DefaultDpi : dpi;
        return new POINT
        {
            X = Math.Max(existing.X, DipsToPixels(minWidthDips, effectiveDpi)),
            Y = Math.Max(existing.Y, DipsToPixels(minHeightDips, effectiveDpi)),
        };
    }

    private static int DipsToPixels(double dips, uint dpi)
    {
        if (!double.IsFinite(dips) || dips <= 0) return 0;
        var pixels = Math.Ceiling(dips * dpi / DefaultDpi);
        return pixels >= int.MaxValue ? int.MaxValue : (int)pixels;
    }

    internal static (int Width, int Height) CalculateMinimumTrackSizeForTests(
        int existingWidthPixels,
        int existingHeightPixels,
        double minWidthDips,
        double minHeightDips,
        uint dpi)
    {
        var result = CalculateMinimumTrackSize(
            new POINT { X = existingWidthPixels, Y = existingHeightPixels },
            minWidthDips,
            minHeightDips,
            dpi);
        return (result.X, result.Y);
    }

    internal static (int X, int Y, int Width, int Height) CalculateMaximizedBoundsForTests(
        int monitorLeft,
        int monitorTop,
        int monitorRight,
        int monitorBottom,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom)
    {
        var result = CalculateMaximizedBounds(
            new RECT
            {
                Left = monitorLeft,
                Top = monitorTop,
                Right = monitorRight,
                Bottom = monitorBottom,
            },
            new RECT
            {
                Left = workLeft,
                Top = workTop,
                Right = workRight,
                Bottom = workBottom,
            });
        return (result.Position.X, result.Position.Y, result.Size.X, result.Size.Y);
    }

    private static void InstallResizeSubclass(
        IntPtr hwnd,
        Window window,
        Action<bool>? moveSizeStateChanged)
    {
        if (hwnd == IntPtr.Zero) return;
        if (ResizeSubclassStates.ContainsKey(hwnd)) return;

        SubclassProc proc = ResizeSubclassProc;
        var state = new ResizeSubclassState(window, proc, moveSizeStateChanged);
        if (SetWindowSubclass(hwnd, proc, ResizeSubclassId, UIntPtr.Zero))
            ResizeSubclassStates[hwnd] = state;
    }

    internal static bool HasExpandedResizeSubclassForTests(IntPtr hwnd) =>
        ResizeSubclassStates.ContainsKey(hwnd);

    private static IntPtr ResizeSubclassProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (ResizeSubclassStates.TryGetValue(hwnd, out var state))
        {
            state.InMoveSizeLoop = ProcessMoveSizeMessage(
                state.InMoveSizeLoop,
                msg,
                state.MoveSizeStateChanged);

            if (msg == WM_NCHITTEST)
            {
                var hit = HitTestResizeZone(state.Window, lParam);
                if (hit is not null) return new IntPtr(hit.Value);
            }

            if (msg == WM_NCDESTROY)
            {
                RemoveWindowSubclass(hwnd, state.Proc, ResizeSubclassId);
                ResizeSubclassStates.Remove(hwnd);
            }
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Keep managed activity state aligned with the actual native modal move/resize loop. The
    /// callback is deliberately transition-only and exception-contained because it runs inside a
    /// native subclass procedure.
    /// </summary>
    private static bool ProcessMoveSizeMessage(
        bool inMoveSizeLoop,
        int message,
        Action<bool>? moveSizeStateChanged)
    {
        var next = message switch
        {
            WM_ENTERSIZEMOVE => true,
            WM_EXITSIZEMOVE or WM_NCDESTROY => false,
            _ => inMoveSizeLoop,
        };

        if (next == inMoveSizeLoop) return inMoveSizeLoop;
        try { moveSizeStateChanged?.Invoke(next); }
        catch { /* managed exceptions must never escape a native window procedure */ }
        return next;
    }

    internal static bool ProcessMoveSizeMessageForTests(
        bool inMoveSizeLoop,
        int message,
        Action<bool>? moveSizeStateChanged) =>
        ProcessMoveSizeMessage(inMoveSizeLoop, message, moveSizeStateChanged);

    private static int? HitTestResizeZone(Window window, IntPtr lParam)
    {
        if (!IsResizable(window)) return null;
        // Teardown: a WM_NCHITTEST can arrive after the HwndSource is disconnected but before
        // WM_NCDESTROY removes the subclass — PointFromScreen would throw across the native frame.
        if (PresentationSource.FromVisual(window) is null) return null;

        var screen = new Point(GetSignedX(lParam), GetSignedY(lParam));
        var point = window.PointFromScreen(screen);
        var width = ActualOrConfigured(window.ActualWidth, window.Width);
        var height = ActualOrConfigured(window.ActualHeight, window.Height);

        var hit = BorderlessResizeHitTestPolicy.HitTest(
            width,
            height,
            point.X,
            point.Y,
            isResizable: true,
            isNormalWindowState: window.WindowState == WindowState.Normal);
        if (hit is null) return null;

        // The edge bands overlap chrome buttons that sit flush with the window edge (the popout's
        // Close/Pin/Fade). An enabled button under the cursor wins — same precedence native
        // caption buttons get over the resize border.
        return IsOverInteractiveControl(window, point) ? null : hit;
    }

    private static bool IsOverInteractiveControl(Window window, Point point)
    {
        var hit = VisualTreeHelper.HitTest(window, point)?.VisualHit;
        for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase { IsEnabled: true }) return true;
        }
        return false;
    }

    private static bool IsResizable(Window window) =>
        window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

    private static double ActualOrConfigured(double actual, double configured) =>
        actual > 0 ? actual : double.IsNaN(configured) ? 0 : configured;

    private static int GetSignedX(IntPtr lParam) => unchecked((short)lParam.ToInt64());

    private static int GetSignedY(IntPtr lParam) => unchecked((short)(lParam.ToInt64() >> 16));

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData);

    private sealed class ResizeSubclassState(
        Window window,
        SubclassProc proc,
        Action<bool>? moveSizeStateChanged)
    {
        public Window Window { get; } = window;
        public SubclassProc Proc { get; } = proc;
        public Action<bool>? MoveSizeStateChanged { get; } = moveSizeStateChanged;
        public bool InMoveSizeLoop { get; set; }
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hwnd,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hwnd,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
