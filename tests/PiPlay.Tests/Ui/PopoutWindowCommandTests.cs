using System.Windows;
using System.Windows.Controls.Primitives;
using PiPlay;
using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>Popout shortcuts, window title, and the one-shot play nudge (spec 13.2, 20).</summary>
[Trait(TestCategories.Key, TestCategories.Wpf)]
public class PopoutWindowCommandTests
{
    private static PlayerWindow NewPlayer(bool nudgePlayOnInitialPause = true) =>
        new(environment: null!, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false, placement: null, defaultWidth: 960, defaultHeight: 540,
            fadeEnabled: true, nudgePlayOnInitialPause: nudgePlayOnInitialPause);

    [Fact]
    public void F11_expands_and_restores_once_per_press() =>
        StaTestThread.Invoke(() =>
        {
            var w = NewPlayer();

            Assert.True(w.HandleShortcut(PopoutShortcut.ToggleExpand, isRepeat: false));
            Assert.Equal(WindowState.Maximized, w.WindowState);

            // Auto-repeat forwarded from the page is swallowed, not toggled back.
            Assert.True(w.HandleShortcut(PopoutShortcut.ToggleExpand, isRepeat: false));
            Assert.Equal(WindowState.Maximized, w.WindowState);

            w.ReleaseShortcutForTests();
            Assert.True(w.HandleShortcut(PopoutShortcut.ToggleExpand, isRepeat: false));
            Assert.Equal(WindowState.Normal, w.WindowState);
        });

    [Fact]
    public void Escape_is_consumed_only_when_it_restores() =>
        StaTestThread.Invoke(() =>
        {
            var w = NewPlayer();

            Assert.False(w.HandleShortcut(PopoutShortcut.RestoreFromExpand, isRepeat: false));

            w.HandleShortcut(PopoutShortcut.ToggleExpand, isRepeat: false);
            Assert.True(w.HandleShortcut(PopoutShortcut.RestoreFromExpand, isRepeat: false));
            Assert.Equal(WindowState.Normal, w.WindowState);
        });

    [Fact]
    public void Ctrl_T_pins_through_the_same_path_as_the_button() =>
        StaTestThread.Invoke(() =>
        {
            var w = NewPlayer();
            var pin = (ToggleButton)w.FindName("PinToggle")!;

            Assert.True(w.HandleShortcut(PopoutShortcut.TogglePin, isRepeat: false));

            Assert.True(w.Topmost);
            Assert.True(pin.IsChecked);
            Assert.Equal("Unpin popout from top (Ctrl+T)", pin.ToolTip);
        });

    [Fact]
    public void The_window_title_names_the_video() =>
        StaTestThread.Invoke(() =>
        {
            var w = NewPlayer();
            Assert.Equal(PopoutTitlePolicy.FallbackTitle, w.Title);

            w.ApplyDocumentTitle("(4) Night drive mix - YouTube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
            Assert.Equal("Night drive mix — PiPlay", w.Title);

            w.ApplyDocumentTitle("Sign in - Google Accounts", "https://accounts.google.com/signin");
            Assert.Equal(PopoutTitlePolicy.FallbackTitle, w.Title);
        });

    [Fact]
    public void A_video_already_playing_is_never_nudged_so_the_first_pause_sticks() =>
        StaTestThread.Invoke(() =>
        {
            var w = NewPlayer();

            Assert.False(w.ShouldNudgePlay(firstSamplePaused: false));
            // The user pauses (or the video ends): the poll must not resume it.
            Assert.False(w.ShouldNudgePlay(firstSamplePaused: true));
        });

    [Fact]
    public void A_video_that_came_up_paused_is_nudged_exactly_once() =>
        StaTestThread.Invoke(() =>
        {
            var w = NewPlayer();

            Assert.True(w.ShouldNudgePlay(firstSamplePaused: true));
            Assert.False(w.ShouldNudgePlay(firstSamplePaused: true));
        });

    [Fact]
    public void A_popout_from_a_paused_source_is_never_nudged() =>
        StaTestThread.Invoke(() =>
        {
            var w = NewPlayer(nudgePlayOnInitialPause: false);

            Assert.False(w.ShouldNudgePlay(firstSamplePaused: true));
        });
}
