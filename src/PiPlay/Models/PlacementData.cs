using System.Text.Json.Serialization;

namespace PiPlay.Models;

/// <summary>Serializable rectangle in screen pixels.</summary>
public sealed class RectData
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// Persisted window placement: normal-position bounds plus the monitor identity, so PiPlay
/// can restore to the same monitor and clamp to a visible work area when the monitor layout
/// changes (spec 16.4, REQ-PROFILE-02).
/// <para>
/// Coordinate representation: when <see cref="CoordinateSpace"/> is exactly
/// <see cref="ScreenCoordinateSpace"/>, <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/
/// <see cref="Height"/> are physical pixels in virtual-screen space — the same space as
/// <see cref="MonitorWorkArea"/> and monitor lookup. A <c>null</c> (or unrecognised) marker is a
/// legacy value written before the marker existed: the raw <c>WINDOWPLACEMENT.rcNormalPosition</c>
/// capture, which Windows expresses relative to the primary monitor's work-area origin for
/// ordinary top-level windows (and in screen pixels for <c>WS_EX_TOOLWINDOW</c> windows).
/// <see cref="Services.PlacementMath.ToScreenRect"/> converts legacy data on restore using the
/// current primary work-area origin; <see cref="Services.WindowPlacementService"/> converts back
/// to workspace pixels at the Win32 boundary.
/// </para>
/// </summary>
public sealed class PlacementData
{
    /// <summary>Marker value for <see cref="CoordinateSpace"/>: bounds are virtual-screen pixels.</summary>
    public const string ScreenCoordinateSpace = "screen";

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public string? MonitorDeviceName { get; set; }
    public RectData? MonitorWorkArea { get; set; }
    public double DpiScale { get; set; } = 1.0;

    /// <summary>
    /// <see cref="ScreenCoordinateSpace"/> for screen-pixel bounds; <c>null</c> for legacy
    /// workspace-relative captures (see the class remarks).
    /// </summary>
    public string? CoordinateSpace { get; set; }

    /// <summary>True when the bounds are marked as virtual-screen pixels (not persisted; derived from the marker).</summary>
    [JsonIgnore]
    public bool IsScreenSpace =>
        string.Equals(CoordinateSpace, ScreenCoordinateSpace, StringComparison.Ordinal);
}
