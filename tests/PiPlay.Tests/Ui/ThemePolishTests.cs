using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PiPlay;
using PiPlay.Models;
using PiPlay.Theme;

namespace PiPlay.Tests;

/// <summary>
/// Polish review 2026-09-10 (docs/reviews/review-controller-2026-09-10-piplay-polish.md), F-1 and
/// F-7, on the live WPF Application: the label a filled button actually renders wears the fill's
/// foreground token, and the checked toggle wash reaches the shared resources for every preset.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Wpf)]
public class ThemePolishTests
{
    private static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grandchild in Descendants(child)) yield return grandchild;
        }
    }

    /// <summary>Realize the template and the string Content's generated TextBlock, then return it.</summary>
    private static TextBlock RenderedLabel(Control control)
    {
        control.Measure(new Size(300, 60));
        control.Arrange(new Rect(0, 0, 300, 60));
        control.UpdateLayout();
        return Assert.Single(Descendants(control).OfType<TextBlock>());
    }

    [Theory]
    [InlineData("AccentButton", "OnAccent")]
    [InlineData("DangerButton", "OnDanger")]
    public void Filled_button_string_content_renders_in_the_fill_foreground(string styleKey, string foregroundKey) =>
        StaTestThread.Invoke(() =>
        {
            // F-1: a string Content becomes a TextBlock inside the ContentPresenter, and the app-wide
            // implicit TextBlock style's Foreground setter outranks the inherited TextElement.Foreground,
            // so every Save / Overwrite / OK / Delete label rendered TextPrimary on the accent or danger
            // fill (2.2-3.7:1). The rendered TextBlock itself must carry the fill's foreground token.
            var button = new Button { Content = "Save", Style = (Style)Application.Current.FindResource(styleKey) };
            var label = RenderedLabel(button);
            Assert.Equal(BrushColor(foregroundKey), ((SolidColorBrush)label.Foreground).Color);
        });

    [Fact]
    public void Filled_button_label_follows_a_foreground_replaced_after_realization() =>
        StaTestThread.Invoke(() =>
        {
            // The fix must keep the DynamicResource path alive: an accent change replaces OnAccent, and
            // a realized button's label has to re-resolve with it (the PopOutButton way).
            var original = Application.Current.Resources["OnAccent"];
            // Realized standalone, then hosted as a (never shown) Window's content: an app-resource
            // replacement invalidates resource references by walking Application.Windows, exactly as
            // it reaches the real dialogs; an unrooted button would never hear about it.
            var button = new Button { Content = "Save", Style = (Style)Application.Current.FindResource("AccentButton") };
            var label = RenderedLabel(button);
            var host = new Window { Content = button, Width = 300, Height = 60 };
            try
            {
                var sentinel = Color.FromRgb(0xAB, 0xCD, 0xEF);
                var brush = new SolidColorBrush(sentinel);
                brush.Freeze();
                Application.Current.Resources["OnAccent"] = brush;
                button.UpdateLayout();

                Assert.Equal(sentinel, ((SolidColorBrush)button.Foreground).Color);
                Assert.Equal(sentinel, ((SolidColorBrush)label.Foreground).Color);
            }
            finally
            {
                Application.Current.Resources["OnAccent"] = original;
                host.Close();
            }
        });

    [Fact]
    public void PopOutButton_keeps_its_explicit_label_bindings() =>
        StaTestThread.Invoke(() =>
        {
            // PopOutButton hosts its own StackPanel with explicitly bound TextBlocks; the presenter-scoped
            // fix for string content must leave that path exactly as it was.
            var window = new MainWindow();
            var button = (Button)window.FindName("PopOutButton")!;
            var icon = (TextBlock)window.FindName("PopOutButtonIcon")!;
            var text = (TextBlock)window.FindName("PopOutButtonText")!;
            window.Measure(new Size(1200, 700));
            Assert.Equal(((SolidColorBrush)button.Foreground).Color, ((SolidColorBrush)icon.Foreground).Color);
            Assert.Equal(((SolidColorBrush)button.Foreground).Color, ((SolidColorBrush)text.Foreground).Color);
            Assert.Equal(BrushColor("OnAccent"), ((SolidColorBrush)text.Foreground).Color);
        });

    public static IEnumerable<object[]> PresetIds() => ThemeCatalog.Presets.Select(p => new object[] { p.Id });

    private static void SetPointerState(ButtonBase button, string property, bool value)
    {
        var owner = property == "IsPressed" ? typeof(ButtonBase) : typeof(UIElement);
        var field = owner.GetField(property + "PropertyKey",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        button.SetValue((DependencyPropertyKey)field.GetValue(null)!, value);
    }

    [Theory]
    [InlineData("sharp-dark", "AccentButton")]
    [InlineData("minimal", "AccentButton")]
    [InlineData("soft-glass", "AccentButton")]
    [InlineData("sharp-dark", "DangerButton")]
    [InlineData("minimal", "DangerButton")]
    [InlineData("soft-glass", "DangerButton")]
    public void Hovered_filled_buttons_keep_readable_labels_across_theme_changes(string presetId, string style) =>
        StaTestThread.Invoke(() =>
        {
            var host = new Window();
            try
            {
                var button = new Button
                {
                    Content = "Confirm", Style = (Style)Application.Current.FindResource(style),
                };
                var surface = new Border { Child = button };
                surface.SetResourceReference(Border.BackgroundProperty, "SurfaceBase");
                host.Content = surface;
                var label = RenderedLabel(button);
                SetPointerState(button, "IsMouseOver", true);
                // Reuse the realized control: a live preview must update its hovered ink too.
                foreach (var accent in ThemeCatalog.AccentOptions.Select(a => a.HexColor)
                    .Concat(new[] { "#000000", "#7D787D" }))
                {
                    ThemeResourceApplier.Apply(Application.Current.Resources,
                        new ThemeSettings { ThemeId = presetId, AccentColor = accent }, new PlayerSettings());
                    var fill = SampleFill(surface);
                    var behind = ((SolidColorBrush)surface.Background).Color;
                    var ink = ((SolidColorBrush)label.Foreground).Color;
                    var opacity = 1.0;
                    for (DependencyObject? node = label; node is not null; node = VisualTreeHelper.GetParent(node))
                        if (node is UIElement element) opacity *= element.Opacity;
                    var compositeInk = Color.FromRgb(
                        (byte)Math.Round(ink.R * opacity + behind.R * (1 - opacity)),
                        (byte)Math.Round(ink.G * opacity + behind.G * (1 - opacity)),
                        (byte)Math.Round(ink.B * opacity + behind.B * (1 - opacity)));
                    var ratio = Wcag.ContrastRatio(compositeInk.ToString(), fill.ToString());
                    Assert.True(ratio >= 4.5, $"{presetId}/{accent}/{style}: hovered label {ratio:F2}:1");
                }
                if (style == "AccentButton")
                {
                    SetPointerState(button, "IsPressed", true);
                    button.UpdateLayout();
                    Assert.Equal(BrushColor("OnAccentPressed"), ((SolidColorBrush)label.Foreground).Color);
                    SetPointerState(button, "IsPressed", false);
                }
                SetPointerState(button, "IsMouseOver", false);
                button.UpdateLayout();
                Assert.Equal(BrushColor(style == "AccentButton" ? "OnAccent" : "OnDanger"),
                    ((SolidColorBrush)label.Foreground).Color);
            }
            finally
            {
                host.Close();
                ThemeResourceApplier.Apply(Application.Current.Resources, new ThemeSettings(), new PlayerSettings());
            }
        });

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Apply_writes_the_on_danger_token_chosen_by_contrast_for_the_preset(string presetId) =>
        StaTestThread.Invoke(() =>
        {
            // Danger buttons wore hard-coded white (3.4-3.6:1 on every preset's Danger). OnDanger applies
            // the same readability policy as OnAccent, per preset, as a replaced frozen brush pair.
            var resources = new ResourceDictionary();
            ThemeResourceApplier.Apply(resources, new ThemeSettings { ThemeId = presetId }, new PlayerSettings());
            var palette = ThemeCatalog.PresetFor(presetId).Palette;
            var expected = ThemeColors.PickReadableForeground(ThemeColors.ParseColor(palette.Danger));

            var brush = Assert.IsType<SolidColorBrush>(resources["OnDanger"]);
            Assert.Equal(expected, brush.Color);
            Assert.True(brush.IsFrozen);
            Assert.Equal(expected, (Color)resources["OnDangerColor"]);
            Assert.True(ThemeColors.ContrastRatio(expected, ThemeColors.ParseColor(palette.Danger)) >= 4.5);
        });

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Apply_writes_the_checked_wash_pair_from_the_derived_set(string presetId) =>
        StaTestThread.Invoke(() =>
        {
            var resources = new ResourceDictionary();
            var theme = new ThemeSettings { ThemeId = presetId, AccentColor = "#9E84F0" };
            ThemeResourceApplier.Apply(resources, theme, new PlayerSettings());
            var set = ThemeColors.DeriveAccentSet("#9E84F0", ThemeCatalog.PresetFor(presetId));

            var brush = Assert.IsType<SolidColorBrush>(resources["AccentCheckedWash"]);
            Assert.Equal(set.CheckedWash, brush.Color);
            Assert.True(brush.IsFrozen);
            Assert.Equal(set.CheckedWash, (Color)resources["AccentCheckedWashColor"]);
        });

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Checked_toggles_render_a_visible_fill_step_and_heavier_label(string presetId) =>
        StaTestThread.Invoke(() =>
        {
            var settings = new SettingsWindow(isBrowserReady: true);
            var host = new Window();
            try
            {
                foreach (var accent in ThemeCatalog.AccentOptions)
                {
                    ThemeResourceApplier.Apply(Application.Current.Resources,
                        new ThemeSettings { ThemeId = presetId, AccentColor = accent.HexColor }, new PlayerSettings());
                    foreach (var style in new[] { "PinToggle", "PresetToggle" })
                    {
                        var toggle = new ToggleButton
                        {
                            Content = "Pin", Width = 120, Height = 40, Margin = new Thickness(0),
                            FontFamily = new FontFamily("Segoe UI"), Style = (Style)settings.FindResource(style),
                        };
                        var surface = new Border { Background = (Brush)Application.Current.FindResource("SurfaceBase"), Child = toggle };
                        host.Content = surface;
                        var idle = SampleFill(surface);
                        toggle.IsChecked = true;
                        var active = SampleFill(surface);
                        var delta = ThemeColors.ContrastRatio(idle, active);
                        Assert.True(delta >= 1.10, $"{presetId}/{accent.Key}/{style}: rendered fill step {delta:F2}:1");
                        Assert.Equal(FontWeights.SemiBold, Assert.Single(Descendants(toggle).OfType<TextBlock>()).FontWeight);
                        toggle.IsChecked = false;
                        Assert.Equal(idle, SampleFill(surface));
                    }
                }
            }
            finally
            {
                host.Close();
                settings.Close();
                ThemeResourceApplier.Apply(Application.Current.Resources, new ThemeSettings(), new PlayerSettings());
            }
        });

    public static IEnumerable<object[]> CheckedAccentByPreset() =>
        from preset in ThemeCatalog.Presets
        from accent in ThemeCatalog.AccentOptions.Select(a => a.HexColor).Concat(new[] { "#000000", "#7D787D" })
        select new object[] { preset.Id, accent };

    [Theory]
    [MemberData(nameof(CheckedAccentByPreset))]
    public void Checked_hovered_toggles_keep_glyph_contrast(string presetId, string accent) =>
        StaTestThread.Invoke(() =>
        {
            var host = new Window();
            try
            {
                ThemeResourceApplier.Apply(Application.Current.Resources,
                    new ThemeSettings { ThemeId = presetId, AccentColor = accent }, new PlayerSettings());
                var toggle = new ToggleButton
                {
                    Content = "\uE840", Width = 120, Height = 40,
                    Style = (Style)Application.Current.FindResource("PinToggle"),
                };
                // Source/Popout Pin and Fade assign a local checked brush. Exercise that path,
                // because correcting only AccentPrimary leaves those real glyphs unchanged.
                ToggleAccent.SetCheckedBrush(toggle, ThemeColors.ContrastBrush(accent, BrushColor("SurfaceHover")));
                var surface = new Border { Background = (Brush)Application.Current.FindResource("SurfaceBase"), Child = toggle };
                host.Content = surface;
                toggle.IsChecked = true;
                SetPointerState(toggle, "IsMouseOver", true);
                var fill = SampleFill(surface);
                var glyph = Assert.Single(Descendants(toggle).OfType<TextBlock>());
                var ink = ((SolidColorBrush)glyph.Foreground).Color;

                var ratio = Wcag.ContrastRatio(ink.ToString(), fill.ToString());
                Assert.True(ratio >= 3.0, $"{presetId}/{accent}: checked-hover glyph {ratio:F2}:1");

                toggle.IsChecked = false;
                toggle.UpdateLayout();
                Assert.Equal(BrushColor("TextSecondary"), ((SolidColorBrush)glyph.Foreground).Color);
            }
            finally
            {
                host.Close();
                ThemeResourceApplier.Apply(Application.Current.Resources, new ThemeSettings(), new PlayerSettings());
            }
        });

    private static Color SampleFill(FrameworkElement surface)
    {
        surface.Measure(new Size(120, 40));
        surface.Arrange(new Rect(0, 0, 120, 40));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(120, 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(6, 20, 1, 1), pixel, 4, 0);
        Assert.Equal(255, pixel[3]);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Transitional_toolbar_labels_keep_readable_contrast_while_disabled(string presetId) =>
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow(new AppSettings { Theme = new ThemeSettings { ThemeId = presetId } });
            try
            {
                ThemeResourceApplier.Apply(Application.Current.Resources,
                    new ThemeSettings { ThemeId = presetId }, new PlayerSettings());
                var button = (Button)window.FindName("PopOutButton")!;
                var label = (TextBlock)window.FindName("PopOutButtonText")!;
                window.ApplySourceToolbarLayout(800);
                foreach (var returning in new[] { true, false })
                {
                    if (returning) window.BeginReturnForTests();
                    else { window.CompleteReturnForTests(); window.SetClearingBrowserDataForTests(true); }
                    window.ApplySourceToolbarLayout(800);
                    window.Measure(new Size(800, 700));
                    window.Arrange(new Rect(0, 0, 800, 700));
                    window.UpdateLayout();
                    button.Measure(new Size(200, 40));
                    button.Arrange(new Rect(0, 0, 200, 40));
                    button.UpdateLayout();
                    Assert.False(button.IsEnabled);
                    Assert.Equal(Visibility.Visible, label.Visibility);
                    Assert.Equal(1.0, button.Opacity);
                    var fill = Assert.IsType<Border>(button.Template.FindName("bd", button));
                    Assert.True(ThemeColors.ContrastRatio(((SolidColorBrush)label.Foreground).Color,
                        ((SolidColorBrush)fill.Background).Color) >= 4.5);
                }
                window.SetClearingBrowserDataForTests(false);
                window.ApplySourceToolbarLayout(800);
                Assert.Same(Application.Current.FindResource("AccentButton"), button.Style);
            }
            finally
            {
                window.CompleteReturnForTests();
                window.Close();
                ThemeResourceApplier.Apply(Application.Current.Resources, new ThemeSettings(), new PlayerSettings());
            }
        });

    [Fact]
    public void Preset_chip_label_stays_primary_text_when_checked() =>
        StaTestThread.Invoke(() =>
        {
            // The chip label no longer flips to the accent when checked: on a dim accent that text fell
            // under 4.5:1, and the wash + border already carry the state.
            var settings = new SettingsWindow(isBrowserReady: true);
            var chip = new ToggleButton { Content = "Sharp", Style = (Style)settings.FindResource("PresetToggle") };
            chip.Measure(new Size(200, 40));
            chip.Arrange(new Rect(0, 0, 200, 40));
            chip.IsChecked = true;
            chip.UpdateLayout();
            Assert.Equal(BrushColor("TextPrimary"), ((SolidColorBrush)chip.Foreground).Color);
            settings.Close();
        });
}
