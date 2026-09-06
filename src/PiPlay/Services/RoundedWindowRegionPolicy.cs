using PiPlay.Theme;

namespace PiPlay.Services;

/// <summary>
/// Pure geometry policy for the Popout Player's large rounded-card window region. Win32 regions use
/// physical pixels; theme radii use DIPs. Keeping the conversion here makes the PerMonitorV2 boundary
/// explicit and testable without an HWND.
/// </summary>
internal static class RoundedWindowRegionPolicy
{
    private const double DefaultDpi = 96.0;

    internal readonly record struct Geometry(int WidthPx, int HeightPx, int EllipseWidthPx, int EllipseHeightPx);

    public static bool CanApply(
        DwmCornerMode cornerMode,
        double radiusDip,
        bool isNormalWindowState) =>
        cornerMode == DwmCornerMode.Round &&
        double.IsFinite(radiusDip) &&
        radiusDip > 0 &&
        isNormalWindowState;

    public static bool ShouldApply(
        DwmCornerMode cornerMode,
        double radiusDip,
        bool isNormalWindowState,
        bool isSnapLike) =>
        CanApply(cornerMode, radiusDip, isNormalWindowState) && !isSnapLike;

    public static Geometry? CreateGeometry(int widthPx, int heightPx, double radiusDip, uint dpi)
    {
        if (widthPx <= 0 || heightPx <= 0 || dpi == 0) return null;
        if (!double.IsFinite(radiusDip) || radiusDip <= 0) return null;

        var maxRadiusPx = Math.Min(widthPx, heightPx) / 2;
        if (maxRadiusPx <= 0) return null;

        // Clamp in double space BEFORE any int cast: scaling a huge DIP radius by DPI can
        // overflow to Infinity, and converting a non-finite or out-of-range double to int is
        // unspecified in C# — the clamp itself must produce a finite, in-range value.
        var maxRadius = (double)maxRadiusPx;
        var scaled = radiusDip * dpi / DefaultDpi;
        if (!double.IsFinite(scaled)) scaled = maxRadius;
        scaled = Math.Min(scaled, maxRadius);
        var radiusPx = Math.Clamp(
            (int)Math.Round(scaled, MidpointRounding.AwayFromZero),
            1,
            maxRadiusPx);
        var diameterPx = radiusPx * 2;
        return new Geometry(widthPx, heightPx, diameterPx, diameterPx);
    }

    /// <summary>
    /// Windows does not expose a public snapped-state flag for ordinary top-level HWNDs. Mirror the
    /// common Snap Layout geometries conservatively: the window must align to a horizontal and vertical
    /// work-area edge AND match a standard half/third/quarter layout fraction. An arbitrary floating
    /// window parked in a work-area corner remains rounded.
    /// </summary>
    public static bool IsSnapLike(
        int left,
        int top,
        int right,
        int bottom,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int tolerancePx = 1)
    {
        if (right <= left || bottom <= top || workRight <= workLeft || workBottom <= workTop) return false;
        tolerancePx = Math.Max(0, tolerancePx);

        var touchesHorizontalEdge =
            Math.Abs(left - workLeft) <= tolerancePx || Math.Abs(right - workRight) <= tolerancePx;
        var touchesVerticalEdge =
            Math.Abs(top - workTop) <= tolerancePx || Math.Abs(bottom - workBottom) <= tolerancePx;
        if (!touchesHorizontalEdge || !touchesVerticalEdge) return false;

        var width = right - left;
        var height = bottom - top;
        var workWidth = workRight - workLeft;
        var workHeight = workBottom - workTop;

        var fullHeightColumn = NearFraction(height, workHeight, 1, 1, tolerancePx) &&
            (NearFraction(width, workWidth, 1, 4, tolerancePx) ||
             NearFraction(width, workWidth, 1, 3, tolerancePx) ||
             NearFraction(width, workWidth, 1, 2, tolerancePx) ||
             NearFraction(width, workWidth, 2, 3, tolerancePx) ||
             NearFraction(width, workWidth, 3, 4, tolerancePx));
        var halfHeightTile = NearFraction(height, workHeight, 1, 2, tolerancePx) &&
            (NearFraction(width, workWidth, 1, 2, tolerancePx) ||
             NearFraction(width, workWidth, 1, 1, tolerancePx));
        return fullHeightColumn || halfHeightTile;
    }

    private static bool NearFraction(int value, int whole, int numerator, int denominator, int tolerancePx) =>
        Math.Abs((long)value * denominator - (long)whole * numerator) <= (long)tolerancePx * denominator;
}
