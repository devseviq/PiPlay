using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PiPlay;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Wpf)]
public class PresentationPolishTests
{
    [Fact]
    public void Cancel_discards_the_preview_without_applying() => StaTestThread.Invoke(() =>
    {
        var window = new SettingsWindow(isBrowserReady: true) { Opacity = 0, ShowActivated = false };
        try
        {
            window.Show();
            ((ToggleButton)window.FindName("ThemeSoftGlassPreset")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var cancel = Assert.IsType<Button>(window.FindName("CancelButton"));
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(window.IsVisible);
            Assert.NotEqual(true, window.DialogResult);
        }
        finally { if (window.IsVisible) window.Close(); }
    });

    [Fact]
    public void Themed_slider_keeps_native_range_commands() => StaTestThread.Invoke(() =>
    {
        var slider = new Slider
        {
            Style = (Style)Application.Current.FindResource("DarkSlider"),
            Minimum = 45, Maximum = 100, Value = 82, SmallChange = 1, LargeChange = 10
        };
        slider.ApplyTemplate();
        slider.Measure(new Size(250, 32));
        slider.Arrange(new Rect(0, 0, 250, 32));
        Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
        Slider.IncreaseSmall.Execute(null, slider);
        Assert.Equal(83, slider.Value);
        Slider.DecreaseLarge.Execute(null, slider);
        Assert.Equal(73, slider.Value);
        Slider.MaximizeValue.Execute(null, slider);
        Assert.Equal(100, slider.Value);
    });

    [Theory]
    [InlineData("CornerStyleSquareChip", "square")]
    [InlineData("CornerStyleRoundChip", "round")]
    public void Soft_glass_corner_choice_preserves_translucency(string control, string corner) => StaTestThread.Invoke(() =>
    {
        var window = new SettingsWindow(isBrowserReady: true, themeId: "soft-glass");
        ((ToggleButton)window.FindName(control)!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(corner, window.CornerStyle);
        Assert.Equal("soft-glass", window.ThemeId);
        Assert.Equal(0.82, window.ConstantWindowOpacity);
        Assert.Equal(0.72, window.IdleWindowOpacity);
        Assert.Null(window.ActiveOpacityOverride);
        Assert.Null(window.IdleOpacityOverride);
    });
}
