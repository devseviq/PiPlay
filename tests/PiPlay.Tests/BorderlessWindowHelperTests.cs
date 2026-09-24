using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class BorderlessWindowHelperTests
{
    [Theory]
    [InlineData(96, 760, 480)]
    [InlineData(120, 950, 600)]
    [InlineData(144, 1140, 720)]
    public void Minimum_track_size_enforces_window_dip_floor_at_current_dpi(
        int dpi,
        int expectedWidth,
        int expectedHeight)
    {
        var actual = BorderlessWindowHelper.CalculateMinimumTrackSizeForTests(
            existingWidthPixels: 120,
            existingHeightPixels: 80,
            minWidthDips: 760,
            minHeightDips: 480,
            dpi: (uint)dpi);

        Assert.Equal(expectedWidth, actual.Width);
        Assert.Equal(expectedHeight, actual.Height);
    }

    [Fact]
    public void Minimum_track_size_preserves_a_stricter_native_floor_per_axis()
    {
        var actual = BorderlessWindowHelper.CalculateMinimumTrackSizeForTests(
            existingWidthPixels: 1300,
            existingHeightPixels: 80,
            minWidthDips: 760,
            minHeightDips: 480,
            dpi: 144);

        Assert.Equal(1300, actual.Width);
        Assert.Equal(720, actual.Height);
    }

    [Fact]
    public void Minimum_track_size_rounds_fractional_device_pixels_up()
    {
        var actual = BorderlessWindowHelper.CalculateMinimumTrackSizeForTests(
            existingWidthPixels: 0,
            existingHeightPixels: 0,
            minWidthDips: 760.1,
            minHeightDips: 480.1,
            dpi: 120);

        Assert.Equal(951, actual.Width);
        Assert.Equal(601, actual.Height);
    }

    [Fact]
    public void Maximized_bounds_remain_relative_to_the_monitor_work_area()
    {
        var actual = BorderlessWindowHelper.CalculateMaximizedBoundsForTests(
            monitorLeft: -1920,
            monitorTop: -100,
            monitorRight: 0,
            monitorBottom: 1080,
            workLeft: -1900,
            workTop: -60,
            workRight: -20,
            workBottom: 1040);

        Assert.Equal(20, actual.X);
        Assert.Equal(40, actual.Y);
        Assert.Equal(1880, actual.Width);
        Assert.Equal(1100, actual.Height);
    }

    [Fact]
    public void Native_move_is_queued_as_an_asynchronous_system_command()
    {
        var expectedHwnd = new IntPtr(42);
        IntPtr actualHwnd = IntPtr.Zero;
        var actualMessage = 0;
        IntPtr actualCommand = IntPtr.Zero;
        IntPtr actualPoint = IntPtr.Zero;

        var queued = BorderlessWindowHelper.QueueWindowMoveForTests(
            expectedHwnd,
            -1920,
            340,
            (hwnd, message, command, point) =>
            {
                actualHwnd = hwnd;
                actualMessage = message;
                actualCommand = command;
                actualPoint = point;
                return true;
            });

        Assert.True(queued);
        Assert.Equal(expectedHwnd, actualHwnd);
        Assert.Equal(0x0112, actualMessage); // WM_SYSCOMMAND
        Assert.Equal(0xF012, actualCommand.ToInt32()); // SC_MOVE | HTCAPTION
        Assert.Equal(BorderlessWindowHelper.PackScreenPointForTests(-1920, 340), actualPoint);
    }

    [Fact]
    public void Native_move_does_not_post_without_a_window_handle()
    {
        var posts = 0;

        Assert.False(BorderlessWindowHelper.QueueWindowMoveForTests(
            IntPtr.Zero, 0, 0, (_, _, _, _) => { posts++; return true; }));
        Assert.Equal(0, posts);
    }

    [Theory]
    [InlineData(120, 340)]
    [InlineData(-1920, -140)]
    [InlineData(32767, -32768)]
    public void Native_move_point_preserves_signed_multi_monitor_coordinates(int x, int y)
    {
        var packed = unchecked((int)BorderlessWindowHelper.PackScreenPointForTests(x, y).ToInt64());

        Assert.Equal(unchecked((short)x), unchecked((short)(packed & 0xFFFF)));
        Assert.Equal(unchecked((short)y), unchecked((short)((packed >> 16) & 0xFFFF)));
    }

    [Fact]
    public void Native_move_size_lifecycle_reports_only_real_state_transitions()
    {
        var transitions = new List<bool>();
        var inMoveSizeLoop = false;

        inMoveSizeLoop = BorderlessWindowHelper.ProcessMoveSizeMessageForTests(
            inMoveSizeLoop, 0x0231, transitions.Add); // WM_ENTERSIZEMOVE
        inMoveSizeLoop = BorderlessWindowHelper.ProcessMoveSizeMessageForTests(
            inMoveSizeLoop, 0x0231, transitions.Add); // duplicate enter
        inMoveSizeLoop = BorderlessWindowHelper.ProcessMoveSizeMessageForTests(
            inMoveSizeLoop, 0x0232, transitions.Add); // WM_EXITSIZEMOVE
        inMoveSizeLoop = BorderlessWindowHelper.ProcessMoveSizeMessageForTests(
            inMoveSizeLoop, 0x0082, transitions.Add); // WM_NCDESTROY after exit

        Assert.False(inMoveSizeLoop);
        Assert.Equal([true, false], transitions);
    }

    [Fact]
    public void Native_move_size_lifecycle_contains_callback_failures()
    {
        var exception = Record.Exception(() =>
            BorderlessWindowHelper.ProcessMoveSizeMessageForTests(
                inMoveSizeLoop: false,
                message: 0x0231,
                _ => throw new InvalidOperationException("test callback failure")));

        Assert.Null(exception);
    }

    [Fact]
    public void Native_destroy_ends_an_active_move_size_lifecycle()
    {
        var transitions = new List<bool>();
        var active = BorderlessWindowHelper.ProcessMoveSizeMessageForTests(
            inMoveSizeLoop: false, message: 0x0231, transitions.Add);

        active = BorderlessWindowHelper.ProcessMoveSizeMessageForTests(
            active, message: 0x0082, transitions.Add);

        Assert.False(active);
        Assert.Equal([true, false], transitions);
    }

    [Theory]
    [InlineData(false, 0x01)]   // VK_LBUTTON
    [InlineData(true, 0x02)]    // VK_RBUTTON: swapped buttons make the physical right button primary
    public void Surface_drag_reads_the_logical_primary_button(bool swapped, int expectedVirtualKey) =>
        Assert.Equal(expectedVirtualKey, BorderlessWindowHelper.PrimaryButtonVirtualKey(swapped));
}
