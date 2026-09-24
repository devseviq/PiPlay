using PiPlay.Services;
using Xunit;

namespace PiPlay.Tests;

/// <summary>Corner parking and 16:9 size presets for the Popout (spec 16.4).</summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class PlacementArrangeTests
{
    // A 1920x1040 work area on a secondary monitor to the right (taskbar at the bottom).
    private static readonly RectI Work = new(1920, 0, 3840, 1040);
    private static readonly RectI Window = new(2400, 300, 2880, 572);   // 480 x 272

    [Theory]
    [InlineData(ScreenCorner.TopLeft, 1944, 24)]
    [InlineData(ScreenCorner.TopRight, 3336, 24)]
    [InlineData(ScreenCorner.BottomLeft, 1944, 744)]
    [InlineData(ScreenCorner.BottomRight, 3336, 744)]
    public void Corner_parking_keeps_size_and_leaves_the_margin(ScreenCorner corner, int left, int top)
    {
        var parked = PlacementMath.AlignToCorner(Window, Work, corner, marginPx: 24);

        Assert.Equal(new RectI(left, top, left + 480, top + 272), parked);
    }

    [Fact]
    public void Corner_parking_never_touches_the_work_area_edges()
    {
        // Touching two edges is what the rounded-region policy reads as a snapped window.
        foreach (var corner in Enum.GetValues<ScreenCorner>())
        {
            var parked = PlacementMath.AlignToCorner(Window, Work, corner, marginPx: 16);
            Assert.True(parked.Left > Work.Left && parked.Right < Work.Right);
            Assert.True(parked.Top > Work.Top && parked.Bottom < Work.Bottom);
            Assert.False(RoundedWindowRegionPolicy.IsSnapLike(
                parked.Left, parked.Top, parked.Right, parked.Bottom,
                Work.Left, Work.Top, Work.Right, Work.Bottom));
        }
    }

    [Fact]
    public void Corner_parking_shrinks_a_window_larger_than_the_work_area()
    {
        var huge = new RectI(0, 0, 5000, 3000);

        var parked = PlacementMath.AlignToCorner(huge, Work, ScreenCorner.BottomRight, marginPx: 20);

        Assert.Equal(new RectI(1940, 20, 3820, 1020), parked);
    }

    [Fact]
    public void Corner_margin_is_capped_on_a_tiny_work_area()
    {
        var tiny = new RectI(0, 0, 400, 200);

        var parked = PlacementMath.AlignToCorner(new RectI(0, 0, 100, 50), tiny, ScreenCorner.TopLeft, marginPx: 500);

        Assert.Equal(new RectI(50, 50, 150, 100), parked);   // margin capped at a quarter of 200
    }

    [Fact]
    public void Size_preset_gives_the_video_area_sixteen_by_nine()
    {
        // 640 px video + 1 px frame each side; 360 px video + 44 px strip + 2 px frame.
        var sized = PlacementMath.ResizeToVideoWidth(
            new RectI(2000, 100, 2480, 372), Work, videoWidthPx: 640, chromeHeightPx: 44, frameThicknessPx: 1);

        Assert.Equal(642, sized.Width);
        Assert.Equal(360 + 44 + 2, sized.Height);
        Assert.Equal(new RectI(2000, 100, 2642, 506), sized);   // top-left quadrant: grows right/down
    }

    [Fact]
    public void Size_preset_keeps_a_bottom_right_parked_window_in_its_corner()
    {
        var parked = PlacementMath.AlignToCorner(Window, Work, ScreenCorner.BottomRight, marginPx: 24);

        var sized = PlacementMath.ResizeToVideoWidth(parked, Work, videoWidthPx: 960, chromeHeightPx: 0, frameThicknessPx: 1);

        Assert.Equal(parked.Right, sized.Right);
        Assert.Equal(parked.Bottom, sized.Bottom);
        Assert.Equal(962, sized.Width);
        Assert.Equal(540 + 2, sized.Height);
    }

    [Fact]
    public void Size_preset_is_clamped_into_the_work_area()
    {
        var sized = PlacementMath.ResizeToVideoWidth(
            new RectI(3500, 800, 3800, 1000), Work, videoWidthPx: 4000, chromeHeightPx: 44, frameThicknessPx: 1);

        Assert.True(sized.Left >= Work.Left && sized.Right <= Work.Right);
        Assert.True(sized.Top >= Work.Top && sized.Bottom <= Work.Bottom);
    }
}
