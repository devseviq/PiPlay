using PiPlay.Models;
using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class PlacementMathTests
{
    private static readonly RectI Work = new(0, 0, 1920, 1080);

    [Fact]
    public void Inside_work_area_is_unchanged()
    {
        var r = new RectI(100, 100, 1060, 640); // 960x540 fully inside
        Assert.Equal(r, PlacementMath.Clamp(r, Work));
    }

    [Fact]
    public void Offscreen_right_is_pulled_back_onto_the_monitor()
    {
        var r = new RectI(1900, 100, 2860, 640); // 960 wide, starts past the right edge
        var c = PlacementMath.Clamp(r, Work);
        Assert.True(c.Right <= Work.Right);
        Assert.Equal(960, c.Width); // size preserved
        Assert.Equal(100, c.Top);   // vertical position unchanged
    }

    [Fact]
    public void Negative_origin_is_clamped_to_work_origin()
    {
        var r = new RectI(-500, -500, 460, 40);
        var c = PlacementMath.Clamp(r, Work);
        Assert.Equal(0, c.Left);
        Assert.Equal(0, c.Top);
    }

    [Fact]
    public void Window_larger_than_work_area_is_shrunk_to_fit()
    {
        var r = new RectI(0, 0, 4000, 3000);
        var c = PlacementMath.Clamp(r, Work);
        Assert.Equal(1920, c.Width);
        Assert.Equal(1080, c.Height);
    }

    [Fact]
    public void Clamps_onto_a_secondary_monitor_work_area()
    {
        // Saved on a monitor to the right; clamp must keep it within that monitor's work rect.
        var work2 = new RectI(1920, 0, 3840, 1080);
        var r = new RectI(3800, 1000, 4760, 1540); // hanging off the bottom-right of monitor 2
        var c = PlacementMath.Clamp(r, work2);
        Assert.True(c.Left >= work2.Left && c.Right <= work2.Right);
        Assert.True(c.Top >= work2.Top && c.Bottom <= work2.Bottom);
    }

    // --- EnsureMinSize: raise a saved sub-minimum placement up to the mode floor (compact 480x270) ---

    [Fact]
    public void EnsureMinSize_raises_a_sub_minimum_placement_to_the_floor()
    {
        // A normal-mode 320x180 placement reopened in compact mode (480x270) must clamp up.
        var saved = new PlacementData { X = 100, Y = 50, Width = 320, Height = 180, DpiScale = 1.0 };

        var clamped = PlacementMath.EnsureMinSize(saved, 480, 270);

        Assert.Equal(480, clamped.Width);
        Assert.Equal(270, clamped.Height);
        Assert.Equal(100, clamped.X);   // position preserved
        Assert.Equal(50, clamped.Y);
    }

    [Fact]
    public void EnsureMinSize_leaves_an_already_large_placement_unchanged()
    {
        var saved = new PlacementData { X = 0, Y = 0, Width = 960, Height = 540, DpiScale = 1.0 };
        var clamped = PlacementMath.EnsureMinSize(saved, 480, 270);
        Assert.Equal(960, clamped.Width);
        Assert.Equal(540, clamped.Height);
    }

    [Fact]
    public void EnsureMinSize_converts_the_dip_minimum_with_the_saved_dpi_scale()
    {
        // Physical px bounds at 150%: the 480x270 DIP floor is 720x405 physical px.
        var saved = new PlacementData { Width = 600, Height = 300, DpiScale = 1.5 };
        var clamped = PlacementMath.EnsureMinSize(saved, 480, 270);
        Assert.Equal(720, clamped.Width);
        Assert.Equal(405, clamped.Height);
    }

    [Fact]
    public void EnsureMinSize_treats_a_non_positive_dpi_scale_as_one()
    {
        // Older/partial saved data with DpiScale = 0 must not zero the floor.
        var saved = new PlacementData { Width = 100, Height = 100, DpiScale = 0 };
        var clamped = PlacementMath.EnsureMinSize(saved, 480, 270);
        Assert.Equal(480, clamped.Width);
        Assert.Equal(270, clamped.Height);
    }

    [Fact]
    public void EnsureMinSize_preserves_monitor_and_maximized_metadata()
    {
        var saved = new PlacementData
        {
            X = 10, Y = 20, Width = 100, Height = 100, Maximized = true,
            MonitorDeviceName = @"\\.\DISPLAY2",
            MonitorWorkArea = new RectData { X = 1920, Y = 0, Width = 1920, Height = 1080 },
            DpiScale = 1.0,
        };

        var clamped = PlacementMath.EnsureMinSize(saved, 480, 270);

        Assert.True(clamped.Maximized);
        Assert.Equal(@"\\.\DISPLAY2", clamped.MonitorDeviceName);
        Assert.NotNull(clamped.MonitorWorkArea);
        Assert.Equal(1920, clamped.MonitorWorkArea!.X);
    }

    [Fact]
    public void EnsureMinSize_carries_the_coordinate_space_marker()
    {
        // A screen-space capture must not be demoted to legacy (and re-offset) by the size floor.
        var saved = new PlacementData
        {
            Width = 100, Height = 100, DpiScale = 1.0,
            CoordinateSpace = PlacementData.ScreenCoordinateSpace,
        };
        var clamped = PlacementMath.EnsureMinSize(saved, 480, 270);
        Assert.Equal(PlacementData.ScreenCoordinateSpace, clamped.CoordinateSpace);
        Assert.True(clamped.IsScreenSpace);

        // Legacy stays legacy: the restore path still owes it the workspace->screen lift.
        var legacy = PlacementMath.EnsureMinSize(new PlacementData { Width = 100, Height = 100 }, 480, 270);
        Assert.Null(legacy.CoordinateSpace);
        Assert.False(legacy.IsScreenSpace);
    }

    // --- ForNextLaunch: a closed-expanded popout must not relaunch expanded (overhaul Task 4) ---

    [Fact]
    public void ForNextLaunch_drops_only_the_maximized_flag()
    {
        var captured = new PlacementData
        {
            X = 100, Y = 50, Width = 960, Height = 540, Maximized = true,
            MonitorDeviceName = @"\\.\DISPLAY2",
            MonitorWorkArea = new RectData { X = 1920, Y = 0, Width = 1920, Height = 1080 },
            DpiScale = 1.5,
            CoordinateSpace = PlacementData.ScreenCoordinateSpace,
        };

        var next = PlacementMath.ForNextLaunch(captured)!;

        Assert.False(next.Maximized);
        Assert.Equal(PlacementData.ScreenCoordinateSpace, next.CoordinateSpace);
        // The captured bounds are the prior NORMAL rectangle (rcNormalPosition) — they all survive.
        Assert.Equal(100, next.X);
        Assert.Equal(50, next.Y);
        Assert.Equal(960, next.Width);
        Assert.Equal(540, next.Height);
        Assert.Equal(@"\\.\DISPLAY2", next.MonitorDeviceName);
        Assert.Equal(1920, next.MonitorWorkArea!.X);
        Assert.Equal(1080, next.MonitorWorkArea.Height);
        Assert.Equal(1.5, next.DpiScale);

        // Pure copy (adopted from the b35c0dd landing): the saved input is untouched and the
        // result shares no mutable parts with it.
        Assert.True(captured.Maximized);
        Assert.NotSame(captured.MonitorWorkArea, next.MonitorWorkArea);
    }

    [Fact]
    public void ForNextLaunch_passes_null_through()
    {
        // No capture (e.g. the window never got an HWND) stays no capture.
        Assert.Null(PlacementMath.ForNextLaunch(null));
    }

    // --- Coordinate spaces (PP-07): rcNormalPosition is workspace-relative for ordinary windows and
    // screen-relative for WS_EX_TOOLWINDOW; persisted/lookup/clamp all use screen pixels. ---

    // Primary work areas for a 1920x1080 primary monitor with the taskbar on each edge.
    private static readonly RectI BottomTaskbar = new(0, 0, 1920, 1040);   // origin (0,0)
    private static readonly RectI TopTaskbar = new(0, 40, 1920, 1080);     // origin (0,40)
    private static readonly RectI LeftTaskbar = new(60, 0, 1920, 1080);    // origin (60,0)

    [Fact]
    public void Top_taskbar_offsets_workspace_to_screen_by_the_work_area_origin()
    {
        var workspace = new RectI(100, 100, 1060, 640);
        var screen = PlacementMath.WorkspaceToScreen(workspace, TopTaskbar, toolWindow: false);
        Assert.Equal(new RectI(100, 140, 1060, 680), screen);
        Assert.Equal(workspace.Width, screen.Width);
        Assert.Equal(workspace.Height, screen.Height);
    }

    [Fact]
    public void Left_taskbar_offsets_workspace_to_screen_horizontally()
    {
        var workspace = new RectI(100, 100, 1060, 640);
        var screen = PlacementMath.WorkspaceToScreen(workspace, LeftTaskbar, toolWindow: false);
        Assert.Equal(new RectI(160, 100, 1120, 640), screen);
    }

    [Fact]
    public void Screen_to_workspace_is_the_inverse_offset()
    {
        var screen = new RectI(100, 140, 1060, 680);
        Assert.Equal(new RectI(100, 100, 1060, 640),
            PlacementMath.ScreenToWorkspace(screen, TopTaskbar, toolWindow: false));
        Assert.Equal(new RectI(40, 140, 1000, 680),
            PlacementMath.ScreenToWorkspace(screen, LeftTaskbar, toolWindow: false));
    }

    [Fact]
    public void Bottom_taskbar_origin_is_zero_so_workspace_and_screen_coincide()
    {
        var r = new RectI(100, 100, 1060, 640);
        Assert.Equal(r, PlacementMath.WorkspaceToScreen(r, BottomTaskbar, toolWindow: false));
        Assert.Equal(r, PlacementMath.ScreenToWorkspace(r, BottomTaskbar, toolWindow: false));
    }

    [Fact]
    public void Tool_window_placement_is_already_screen_space_and_is_not_offset()
    {
        // WS_EX_TOOLWINDOW: Windows reports/accepts rcNormalPosition in screen pixels.
        var r = new RectI(100, 100, 1060, 640);
        Assert.Equal(r, PlacementMath.WorkspaceToScreen(r, TopTaskbar, toolWindow: true));
        Assert.Equal(r, PlacementMath.ScreenToWorkspace(r, LeftTaskbar, toolWindow: true));

        // Same input, ordinary window: the offset applies. The flag is the only difference.
        Assert.NotEqual(r, PlacementMath.WorkspaceToScreen(r, TopTaskbar, toolWindow: false));
    }

    [Fact]
    public void Negative_monitor_origin_converts_and_clamps_in_screen_space()
    {
        // Secondary monitor LEFT of the primary (x = -1920..0), primary taskbar on top.
        var work2 = new RectI(-1920, 0, 0, 1080);
        var screen = new RectI(-1800, 100, -840, 640); // 960x540 on the secondary

        // Workspace form subtracts the primary origin; the negative x is untouched by a y-only offset.
        var workspace = PlacementMath.ScreenToWorkspace(screen, TopTaskbar, toolWindow: false);
        Assert.Equal(new RectI(-1800, 60, -840, 600), workspace);
        Assert.Equal(screen, PlacementMath.WorkspaceToScreen(workspace, TopTaskbar, toolWindow: false));

        // Clamping runs against the secondary's screen-space work area and leaves an inside rect alone.
        Assert.Equal(screen, PlacementMath.Clamp(screen, work2));

        // A rect hanging off the secondary's left edge is pulled back inside that monitor, not the primary.
        var hanging = new RectI(-2400, 100, -1440, 640);
        var c = PlacementMath.Clamp(hanging, work2);
        Assert.Equal(-1920, c.Left);
        Assert.True(c.Right <= work2.Right);
        Assert.Equal(960, c.Width);
    }

    [Theory]
    [InlineData(0, 0)]      // bottom/right taskbar
    [InlineData(0, 40)]     // top taskbar
    [InlineData(60, 0)]     // left taskbar
    [InlineData(-1920, 0)]  // synthetic negative offset: the conversion is a pure translation
    public void Round_trip_screen_to_workspace_to_screen_is_identity(int originX, int originY)
    {
        // Repeated save/restore must not drift: capture (workspace->screen) then restore
        // (screen->workspace) then capture again lands on the same screen rectangle.
        var primary = new RectI(originX, originY, originX + 1920, originY + 1040);
        var screen = new RectI(-300, 250, 660, 790);
        foreach (var tool in new[] { false, true })
        {
            var workspace = PlacementMath.ScreenToWorkspace(screen, primary, tool);
            Assert.Equal(screen, PlacementMath.WorkspaceToScreen(workspace, primary, tool));
            Assert.Equal(workspace, PlacementMath.ScreenToWorkspace(
                PlacementMath.WorkspaceToScreen(workspace, primary, tool), primary, tool));
        }
    }

    [Fact]
    public void Legacy_unmarked_data_is_read_as_a_workspace_relative_capture()
    {
        // Pre-marker settings stored the raw rcNormalPosition; with a top taskbar that value was
        // 40px above where it appeared on screen. ToScreenRect lifts it by the current origin.
        var legacy = new PlacementData { X = 100, Y = 100, Width = 960, Height = 540 };
        Assert.Null(legacy.CoordinateSpace);
        Assert.False(legacy.IsScreenSpace);

        Assert.Equal(new RectI(100, 140, 1060, 680),
            PlacementMath.ToScreenRect(legacy, TopTaskbar, toolWindow: false));
        Assert.Equal(new RectI(160, 100, 1120, 640),
            PlacementMath.ToScreenRect(legacy, LeftTaskbar, toolWindow: false));

        // Bottom/right taskbar: origin (0,0), so legacy values keep working unchanged.
        Assert.Equal(new RectI(100, 100, 1060, 640),
            PlacementMath.ToScreenRect(legacy, BottomTaskbar, toolWindow: false));

        // A legacy capture of a tool window was already screen pixels.
        Assert.Equal(new RectI(100, 100, 1060, 640),
            PlacementMath.ToScreenRect(legacy, TopTaskbar, toolWindow: true));
    }

    [Fact]
    public void Marked_screen_data_is_used_without_conversion()
    {
        var marked = new PlacementData
        {
            X = 100, Y = 140, Width = 960, Height = 540,
            CoordinateSpace = PlacementData.ScreenCoordinateSpace,
        };
        Assert.True(marked.IsScreenSpace);
        var expected = new RectI(100, 140, 1060, 680);
        Assert.Equal(expected, PlacementMath.ToScreenRect(marked, TopTaskbar, toolWindow: false));
        Assert.Equal(expected, PlacementMath.ToScreenRect(marked, LeftTaskbar, toolWindow: false));
        Assert.Equal(expected, PlacementMath.ToScreenRect(marked, TopTaskbar, toolWindow: true));
    }

    [Fact]
    public void Unrecognised_marker_falls_back_to_legacy_semantics()
    {
        // Only the exact "screen" marker (ordinal) is trusted; anything else is the raw capture.
        var odd = new PlacementData { X = 100, Y = 100, Width = 960, Height = 540, CoordinateSpace = "Screen" };
        Assert.False(odd.IsScreenSpace);
        Assert.Equal(new RectI(100, 140, 1060, 680),
            PlacementMath.ToScreenRect(odd, TopTaskbar, toolWindow: false));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.5)]
    public void Coordinate_conversion_is_pixel_only_and_independent_of_dpi_scale(double dpiScale)
    {
        // Placement bounds are physical pixels and the workspace offset is physical pixels; the
        // saved DPI scale must not scale the offset (it exists only for the DIP minimum-size floor).
        var legacy = new PlacementData { X = 100, Y = 100, Width = 960, Height = 540, DpiScale = dpiScale };
        Assert.Equal(new RectI(100, 140, 1060, 680),
            PlacementMath.ToScreenRect(legacy, TopTaskbar, toolWindow: false));

        var marked = new PlacementData
        {
            X = 100, Y = 140, Width = 960, Height = 540, DpiScale = dpiScale,
            CoordinateSpace = PlacementData.ScreenCoordinateSpace,
        };
        Assert.Equal(new RectI(100, 140, 1060, 680),
            PlacementMath.ToScreenRect(marked, TopTaskbar, toolWindow: false));
    }
}
