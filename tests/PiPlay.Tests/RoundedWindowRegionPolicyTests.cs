using PiPlay.Services;
using PiPlay.Theme;
using Xunit;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class RoundedWindowRegionPolicyTests
{
    [Theory]
    [InlineData(DwmCornerMode.Round, 22, true, false, true)]
    [InlineData(DwmCornerMode.Round, 22, false, false, false)]
    [InlineData(DwmCornerMode.Round, 22, true, true, false)]
    [InlineData(DwmCornerMode.Round, 0, true, false, false)]
    [InlineData(DwmCornerMode.SmallRound, 22, true, false, false)]
    [InlineData(DwmCornerMode.Square, 22, true, false, false)]
    [InlineData(DwmCornerMode.Default, 22, true, false, false)]
    public void ShouldApply_limits_custom_regions_to_floating_round_windows(
        DwmCornerMode mode,
        double radiusDip,
        bool normal,
        bool snapLike,
        bool expected) =>
        Assert.Equal(expected, RoundedWindowRegionPolicy.ShouldApply(mode, radiusDip, normal, snapLike));

    [Theory]
    [InlineData(DwmCornerMode.Round, 22, true, true)]
    [InlineData(DwmCornerMode.Round, 22, false, false)]
    [InlineData(DwmCornerMode.Round, 0, true, false)]
    [InlineData(DwmCornerMode.SmallRound, 22, true, false)]
    [InlineData(DwmCornerMode.Square, 22, true, false)]
    [InlineData(DwmCornerMode.Default, 22, true, false)]
    public void CanApply_identifies_when_snap_classification_is_worth_the_native_calls(
        DwmCornerMode mode,
        double radiusDip,
        bool normal,
        bool expected) =>
        Assert.Equal(expected, RoundedWindowRegionPolicy.CanApply(mode, radiusDip, normal));

    [Theory]
    [InlineData(96, 44)]
    [InlineData(120, 56)]
    [InlineData(144, 66)]
    [InlineData(192, 88)]
    public void Geometry_scales_the_22_dip_radius_to_device_pixels(uint dpi, int expectedDiameter)
    {
        var geometry = RoundedWindowRegionPolicy.CreateGeometry(960, 540, 22, dpi);

        Assert.NotNull(geometry);
        Assert.Equal(new RoundedWindowRegionPolicy.Geometry(960, 540, expectedDiameter, expectedDiameter),
            geometry.Value);
    }

    [Fact]
    public void Geometry_clamps_the_radius_to_half_the_short_edge()
    {
        var geometry = RoundedWindowRegionPolicy.CreateGeometry(40, 20, radiusDip: 100, dpi: 192);

        Assert.NotNull(geometry);
        Assert.Equal(20, geometry.Value.EllipseWidthPx);
        Assert.Equal(20, geometry.Value.EllipseHeightPx);
    }

    [Theory]
    [InlineData(0, 540, 22, 96)]
    [InlineData(960, 0, 22, 96)]
    [InlineData(960, 540, 0, 96)]
    [InlineData(960, 540, double.NaN, 96)]
    [InlineData(960, 540, double.PositiveInfinity, 96)]
    [InlineData(960, 540, 22, 0)]
    public void Geometry_rejects_invalid_inputs(int width, int height, double radius, uint dpi) =>
        Assert.Null(RoundedWindowRegionPolicy.CreateGeometry(width, height, radius, dpi));

    [Fact]
    public void Geometry_passes_extreme_dimensions_through_and_clamps_an_absurd_radius()
    {
        // The applier adds 1 to these endpoints for GDI, so the largest dimensions must survive the
        // policy unchanged and the diameter must stay the largest representable value.
        var geometry = RoundedWindowRegionPolicy.CreateGeometry(int.MaxValue, int.MaxValue, double.MaxValue, 96);

        Assert.NotNull(geometry);
        Assert.Equal(int.MaxValue, geometry.Value.WidthPx);
        Assert.Equal(int.MaxValue, geometry.Value.HeightPx);
        Assert.Equal(int.MaxValue - 1, geometry.Value.EllipseWidthPx);
        Assert.Equal(int.MaxValue - 1, geometry.Value.EllipseHeightPx);
    }

    [Theory]
    [InlineData(0, 0, 960, 1040, true)]      // snapped left half
    [InlineData(960, 0, 1920, 520, true)]    // snapped top-right quarter
    [InlineData(0, 0, 640, 1040, true)]      // snapped left third
    [InlineData(0, 0, 960, 540, false)]      // corner-aligned, but not a snap-layout height
    [InlineData(100, 0, 1060, 540, false)]   // top edge only: still a floating rounded window
    [InlineData(0, 100, 960, 640, false)]    // left edge only
    [InlineData(100, 100, 1060, 640, false)] // fully floating
    public void Snap_like_requires_alignment_on_both_work_area_axes(
        int left,
        int top,
        int right,
        int bottom,
        bool expected) =>
        Assert.Equal(expected, RoundedWindowRegionPolicy.IsSnapLike(
            left, top, right, bottom,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1040));
}
