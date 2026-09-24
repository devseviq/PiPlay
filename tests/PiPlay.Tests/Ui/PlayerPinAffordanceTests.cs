using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PiPlay;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Wpf)]
public class PlayerPinAffordanceTests
{
    private static PlayerWindow NewPlayer(bool topmost) =>
        new(environment: null!, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: topmost, placement: null, defaultWidth: 960, defaultHeight: 540,
            fadeEnabled: true);

    [Fact]
    public void Native_popout_pin_names_the_action_for_initial_and_toggled_states() =>
        StaTestThread.Invoke(() =>
        {
            var window = NewPlayer(topmost: false);
            var pin = (ToggleButton)window.FindName("PinToggle")!;

            Assert.Equal("Pin popout on top (Ctrl+T)", pin.ToolTip);
            Assert.Equal("Pin popout on top", AutomationProperties.GetName(pin));

            pin.IsChecked = true;
            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.True(window.Topmost);
            Assert.Equal("Unpin popout from top (Ctrl+T)", pin.ToolTip);
            Assert.Equal("Unpin popout from top", AutomationProperties.GetName(pin));

            pin.IsChecked = false;
            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.False(window.Topmost);
            Assert.Equal("Pin popout on top (Ctrl+T)", pin.ToolTip);
            Assert.Equal("Pin popout on top", AutomationProperties.GetName(pin));
        });

    [Fact]
    public void Native_popout_pin_starts_with_an_unpin_action_when_already_topmost() =>
        StaTestThread.Invoke(() =>
        {
            var window = NewPlayer(topmost: true);
            var pin = (ToggleButton)window.FindName("PinToggle")!;

            Assert.True(window.Topmost);
            Assert.True(pin.IsChecked);
            Assert.Equal("Unpin popout from top (Ctrl+T)", pin.ToolTip);
            Assert.Equal("Unpin popout from top", AutomationProperties.GetName(pin));
        });
}
