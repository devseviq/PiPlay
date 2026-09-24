using PiPlay.Models;

namespace PiPlay.Services;

/// <summary>Integer pixel rectangle (Left/Top inclusive bounds; Width/Height derived from Right/Bottom).</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Pure placement geometry, extracted from <see cref="WindowPlacementService"/> so the
/// "never restore a window off-screen" clamp (spec 16.4, REQ-PROFILE-02) is unit-testable
/// without a live <see cref="System.Windows.Window"/> or monitor enumeration.
/// </summary>
public static class PlacementMath
{
    /// <summary>Clamp <paramref name="r"/> into <paramref name="work"/>: shrink to fit, then keep fully on-screen.</summary>
    public static RectI Clamp(RectI r, RectI work)
    {
        var w = Math.Min(r.Width, work.Width);
        var h = Math.Min(r.Height, work.Height);

        var x = r.Left;
        var y = r.Top;
        if (x < work.Left) x = work.Left;
        if (y < work.Top) y = work.Top;
        if (x + w > work.Right) x = work.Right - w;
        if (y + h > work.Bottom) y = work.Bottom - h;

        return new RectI(x, y, x + w, y + h);
    }

    /// <summary>
    /// Raise a saved placement's size up to a minimum expressed in device-independent pixels (DIP),
    /// using the placement's own DPI scale to convert — placement bounds are physical pixels, the
    /// minimums are DIP (spec 10.2 / 16.1). Position, monitor identity, and maximized state are
    /// preserved. Used so a saved sub-minimum placement (e.g. a normal-mode 320x180 window reopened
    /// in compact mode) restores at least at the mode minimum instead of opening unusably small.
    /// A non-positive saved DPI scale is treated as 1.0 (older/partial data must not zero the floor).
    /// </summary>
    public static PlacementData EnsureMinSize(PlacementData data, int minWidthDip, int minHeightDip)
    {
        var scale = data.DpiScale > 0 ? data.DpiScale : 1.0;
        var minWidthPx = (int)Math.Ceiling(minWidthDip * scale);
        var minHeightPx = (int)Math.Ceiling(minHeightDip * scale);

        return new PlacementData
        {
            X = data.X,
            Y = data.Y,
            Width = Math.Max(data.Width, minWidthPx),
            Height = Math.Max(data.Height, minHeightPx),
            Maximized = data.Maximized,
            MonitorDeviceName = data.MonitorDeviceName,
            MonitorWorkArea = data.MonitorWorkArea,
            DpiScale = data.DpiScale,
            CoordinateSpace = data.CoordinateSpace,
        };
    }

    /// <summary>
    /// Normalize a captured placement for use as the NEXT launch state (overhaul Task 4): closing
    /// an expanded popout must not make the next popout LAUNCH expanded — expansion is a per-session
    /// viewing posture, not a saved layout. Only the flag is dropped; the captured bounds are
    /// already the prior NORMAL rectangle (Win32 rcNormalPosition), so the next popout opens there.
    /// </summary>
    public static PlacementData? ForNextLaunch(PlacementData? data)
    {
        if (data is null) return null;
        return new PlacementData
        {
            X = data.X,
            Y = data.Y,
            Width = data.Width,
            Height = data.Height,
            Maximized = false,
            MonitorDeviceName = data.MonitorDeviceName,
            // Deep copy (adopted from the b35c0dd landing): the result must not alias the saved
            // input — PlacementData is mutable, and a shared RectData would let one consumer's
            // edit silently rewrite the other's snapshot.
            MonitorWorkArea = data.MonitorWorkArea is null
                ? null
                : new RectData
                {
                    X = data.MonitorWorkArea.X,
                    Y = data.MonitorWorkArea.Y,
                    Width = data.MonitorWorkArea.Width,
                    Height = data.MonitorWorkArea.Height,
                },
            DpiScale = data.DpiScale,
            CoordinateSpace = data.CoordinateSpace,
        };
    }

    // --- Coordinate spaces (PP-07): WINDOWPLACEMENT.rcNormalPosition is workspace-relative for
    // ordinary top-level windows (offset by the primary monitor's work-area origin, non-zero with
    // a top/left taskbar) and screen-relative for WS_EX_TOOLWINDOW windows. Persisted placement,
    // monitor lookup, and clamping all use virtual-screen pixels; these conversions sit at the
    // Win32 boundary. They are pixel-only — DPI scale never enters the offset.

    /// <summary>
    /// Convert a <c>rcNormalPosition</c> rectangle to virtual-screen pixels. Ordinary windows are
    /// offset by the primary work-area origin (<paramref name="primaryWorkArea"/>.Left/Top);
    /// a tool window's placement is already screen-relative and passes through unchanged.
    /// </summary>
    public static RectI WorkspaceToScreen(RectI r, RectI primaryWorkArea, bool toolWindow) =>
        toolWindow ? r : Offset(r, primaryWorkArea.Left, primaryWorkArea.Top);

    /// <summary>
    /// Convert virtual-screen pixels back to the <c>rcNormalPosition</c> space expected by
    /// <c>SetWindowPlacement</c>. Exact inverse of <see cref="WorkspaceToScreen"/>.
    /// </summary>
    public static RectI ScreenToWorkspace(RectI r, RectI primaryWorkArea, bool toolWindow) =>
        toolWindow ? r : Offset(r, -primaryWorkArea.Left, -primaryWorkArea.Top);

    /// <summary>
    /// The saved bounds as a virtual-screen rectangle. Marked screen-space data is used as-is;
    /// legacy unmarked data is the raw capture of the same window and is converted with
    /// <see cref="WorkspaceToScreen"/> using the current primary work-area origin (with a
    /// bottom/right taskbar that origin is (0,0), so legacy and screen values coincide).
    /// </summary>
    public static RectI ToScreenRect(PlacementData data, RectI primaryWorkArea, bool toolWindow)
    {
        var r = new RectI(data.X, data.Y, data.X + data.Width, data.Y + data.Height);
        return data.IsScreenSpace ? r : WorkspaceToScreen(r, primaryWorkArea, toolWindow);
    }

    /// <summary>
    /// Park a floating window in a corner of <paramref name="work"/>, <paramref name="marginPx"/>
    /// in from both edges, keeping its size (shrunk only if it cannot fit). The margin keeps the
    /// result off the work-area edges, so a parked Popout is never classified as snapped and keeps
    /// its rounded region (ADR-0008).
    /// </summary>
    public static RectI AlignToCorner(RectI window, RectI work, ScreenCorner corner, int marginPx)
    {
        var margin = Math.Max(0, Math.Min(marginPx, Math.Min(work.Width, work.Height) / 4));
        var w = Math.Min(window.Width, work.Width - 2 * margin);
        var h = Math.Min(window.Height, work.Height - 2 * margin);

        var x = corner is ScreenCorner.TopLeft or ScreenCorner.BottomLeft
            ? work.Left + margin
            : work.Right - margin - w;
        var y = corner is ScreenCorner.TopLeft or ScreenCorner.TopRight
            ? work.Top + margin
            : work.Bottom - margin - h;

        return new RectI(x, y, x + w, y + h);
    }

    /// <summary>
    /// Resize a floating window to <paramref name="videoWidthPx"/> wide with a 16:9 video area,
    /// adding the vertical chrome (<paramref name="chromeHeightPx"/>) and frame
    /// (<paramref name="frameThicknessPx"/> on every side) the video does not use. The anchor
    /// corner nearest the work-area corner stays put, so a Popout parked bottom-right grows up
    /// and left; the result is clamped into <paramref name="work"/>.
    /// </summary>
    public static RectI ResizeToVideoWidth(
        RectI window, RectI work, int videoWidthPx, int chromeHeightPx, int frameThicknessPx)
    {
        var frame = Math.Max(0, frameThicknessPx);
        var videoWidth = Math.Max(1, videoWidthPx);
        var width = videoWidth + 2 * frame;
        var height = (int)Math.Round(videoWidth * 9.0 / 16.0) + Math.Max(0, chromeHeightPx) + 2 * frame;

        var anchorRight = window.Left + window.Width / 2 > work.Left + work.Width / 2;
        var anchorBottom = window.Top + window.Height / 2 > work.Top + work.Height / 2;
        var x = anchorRight ? window.Right - width : window.Left;
        var y = anchorBottom ? window.Bottom - height : window.Top;

        return Clamp(new RectI(x, y, x + width, y + height), work);
    }

    private static RectI Offset(RectI r, int dx, int dy) =>
        new(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);
}

public enum ScreenCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}
