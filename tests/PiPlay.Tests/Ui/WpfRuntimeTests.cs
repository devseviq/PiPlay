using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using PiPlay;
using PiPlay.Models;
using PiPlay.Services;
using PiPlay.Theme;

namespace PiPlay.Tests;

/// <summary>
/// Layer 3 — live WPF on a shared STA thread. Constructs the real windows (never shown, so
/// WebView2/network are untouched) to prove every {StaticResource} resolves at runtime and the
/// burned-in DependencyProperty values hold, plus a RenderTargetBitmap check that the URL text
/// is not clipped to a band at 150% DPI (the affirmative guard for the "rounding = 0" bug).
/// </summary>
[Trait(TestCategories.Key, TestCategories.Wpf)]
public class WpfRuntimeTests : IDisposable
{
    static WpfRuntimeTests() =>
        // Production records no border-suppression entries at all (that record exists only so these
        // tests can observe intent), so the border lane has to arm it explicitly.
        WindowOpacityApplier.EnableBorderSuppressionTrackingForTests();

    private static PlayerWindow NewPlayer() =>
        // environment is only used in InitializePlayerAsync (Loaded), never in the ctor.
        new(environment: null!, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false, placement: null, defaultWidth: 960, defaultHeight: 540, fadeEnabled: true);

    // --- Construction proves runtime resource resolution + template compilation ---

    [Fact]
    public void MainWindow_constructs_without_throwing() => StaTestThread.Invoke(() =>
    {
        var ex = Record.Exception(() => new MainWindow());
        Assert.Null(ex);
    });

    [Fact]
    public void PlayerWindow_constructs_without_throwing() => StaTestThread.Invoke(() =>
    {
        var ex = Record.Exception(() => NewPlayer());
        Assert.Null(ex);
    });

    [Fact]
    public void SettingsWindow_constructs_without_throwing() => StaTestThread.Invoke(() =>
    {
        var ex = Record.Exception(() => new SettingsWindow(isBrowserReady: true));
        Assert.Null(ex);
    });

    // --- Theme resource application (overhaul Task 9): replace the accent entries; DynamicResource
    // consumers re-resolve, including controls already realized in open windows. ---

    [Fact]
    public void ThemeResourceApplier_replaces_the_accent_entries_from_the_theme() => StaTestThread.Invoke(() =>
    {
        var res = new ResourceDictionary();   // empty (startup, before windows): the entries are added
        ThemeResourceApplier.Apply(res, new ThemeSettings { AccentColor = "#38D996" }, new PlayerSettings());

        var primary = (SolidColorBrush)res["AccentPrimary"];
        var light = (SolidColorBrush)res["AccentPrimaryLight"];
        Assert.Equal(Color.FromRgb(0x38, 0xD9, 0x96), primary.Color);   // base takes the accent
        Assert.True(primary.IsFrozen);                                  // shareable across windows
        Assert.True(light.Color.G >= primary.Color.G && light.Color != primary.Color);   // lighter derivation
    });

    [Fact]
    public void ThemeResourceApplier_identical_accent_apply_preserves_all_pair_identities() =>
        StaTestThread.Invoke(() =>
        {
            var resources = new ResourceDictionary();
            var preset = ThemeCatalog.PresetFor("sharp-dark");
            ThemeResourceApplier.ApplyAccentOnly(resources, "#38D996", preset, accentIntensity: 35);
            string[] keys =
            [
                "AccentPrimary", "AccentHover", "AccentPressed", "AccentBorder", "AccentShellTint",
                "AccentChromeGlyph", "OnAccent", "OnAccentPressed", "AccentPrimaryLight",
            ];
            var before = keys
                .SelectMany(key => new[] { key, key + "Color" })
                .ToDictionary(key => key, key => resources[key]);

            ThemeResourceApplier.ApplyAccentOnly(resources, "#38D996", preset, accentIntensity: 35);

            foreach (var (key, value) in before)
                Assert.Same(value, resources[key]);
        });

    [Fact]
    public void ThemeResourceApplier_intensity_only_apply_replaces_only_changed_pairs() =>
        StaTestThread.Invoke(() =>
        {
            var resources = new ResourceDictionary();
            var preset = ThemeCatalog.PresetFor("sharp-dark");
            static Dictionary<string, Color> Expected(DerivedAccentSet set) => new()
            {
                ["AccentPrimary"] = set.Primary,
                ["AccentHover"] = set.Hover,
                ["AccentPressed"] = set.Pressed,
                ["AccentBorder"] = set.Border,
                ["AccentShellTint"] = set.ShellTint,
                ["AccentChromeGlyph"] = set.ChromeGlyph,
                ["OnAccent"] = set.OnAccent,
                ["OnAccentPressed"] = set.OnAccentPressed,
                ["AccentPrimaryLight"] = set.Hover,
            };

            void AssertTransition(int fromIntensity, int toIntensity)
            {
                ThemeResourceApplier.ApplyAccentOnly(
                    resources, "#38D996", preset, accentIntensity: fromIntensity);
                var before = Expected(ThemeColors.DeriveAccentSet("#38D996", preset, fromIntensity));
                var brushes = before.Keys.ToDictionary(
                    key => key,
                    key => Assert.IsType<SolidColorBrush>(resources[key]));
                var after = Expected(ThemeColors.DeriveAccentSet("#38D996", preset, toIntensity));

                ThemeResourceApplier.ApplyAccentOnly(
                    resources, "#38D996", preset, accentIntensity: toIntensity);

                foreach (var (key, expectedColor) in after)
                {
                    var brush = Assert.IsType<SolidColorBrush>(resources[key]);
                    if (before[key] == expectedColor)
                        Assert.Same(brushes[key], brush);
                    else
                        Assert.NotSame(brushes[key], brush);
                    Assert.Equal(expectedColor, brush.Color);
                    Assert.True(brush.IsFrozen, $"{key} was not frozen.");
                    Assert.Equal(expectedColor, Assert.IsType<Color>(resources[key + "Color"]));
                }
            }

            AssertTransition(fromIntensity: 25, toIntensity: 35);
            AssertTransition(fromIntensity: 75, toIntensity: 90);
        });

    [Fact]
    public void ThemeResourceApplier_repairs_missing_or_wrong_typed_accent_entries() =>
        StaTestThread.Invoke(() =>
        {
            var resources = new ResourceDictionary
            {
                ["AccentPrimary"] = "not a brush",
                ["AccentPrimaryColor"] = "not a color",
            };
            var preset = ThemeCatalog.PresetFor("sharp-dark");
            var expected = ThemeColors.DeriveAccentSet("#38D996", preset, 35).Primary;

            ThemeResourceApplier.ApplyAccentOnly(resources, "#38D996", preset, accentIntensity: 35);

            var brush = Assert.IsType<SolidColorBrush>(resources["AccentPrimary"]);
            Assert.Equal(expected, brush.Color);
            Assert.True(brush.IsFrozen);
            Assert.Equal(expected, Assert.IsType<Color>(resources["AccentPrimaryColor"]));
        });

    [Fact]
    public void ThemeResourceApplier_applies_palette_and_radii_from_the_preset() => StaTestThread.Invoke(() =>
    {
        var res = new ResourceDictionary();
        ThemeResourceApplier.Apply(res, new ThemeSettings { ThemeId = "soft-glass" }, new PlayerSettings());

        // Every palette brush key lands as a frozen brush matching the preset palette, with its
        // companion Color token in step.
        var palette = ThemeCatalog.PresetFor("soft-glass").Palette;
        string[] hexes =
        [
            palette.AppBackground, palette.SurfaceBase, palette.SurfaceRaised, palette.SurfaceHover,
            palette.BorderSubtle, palette.BorderStrong, palette.TextPrimary, palette.TextSecondary,
            palette.Danger,
        ];
        for (var i = 0; i < ThemeResourceApplier.PaletteBrushKeys.Length; i++)
        {
            var key = ThemeResourceApplier.PaletteBrushKeys[i];
            var brush = Assert.IsType<SolidColorBrush>(res[key]);
            Assert.True(brush.IsFrozen, $"{key} brush not frozen.");
            Assert.Equal(ThemeColors.ParseColor(hexes[i]), brush.Color);
            Assert.Equal(brush.Color, (Color)res[key + "Color"]);
        }

        // Radius tokens follow the preset's ThemeRadii; the title bar rounds only its top corners;
        // the compatibility aliases ride Input/Button. Expected values derive from the catalog so
        // this apply-mechanism test never re-pins literal radii when the preset profile changes.
        var radii = ThemeCatalog.PresetFor("soft-glass").Radii;
        Assert.Equal(new CornerRadius(radii.PopoutFrame), res["RadiusPopoutFrame"]);
        Assert.Equal(new CornerRadius(radii.TitleBar, radii.TitleBar, 0, 0), res["RadiusTitleBar"]);
        Assert.Equal(new CornerRadius(radii.Button), res["RadiusButton"]);
        Assert.Equal(res["RadiusInput"], res["ControlCornerRadius"]);
        Assert.Equal(res["RadiusButton"], res["ButtonCornerRadius"]);

        // The corner-style override swaps radii but never the palette.
        var overridden = new ResourceDictionary();
        ThemeResourceApplier.Apply(overridden,
            new ThemeSettings { ThemeId = "soft-glass", CornerStyle = "square" }, new PlayerSettings());
        Assert.Equal(new CornerRadius(0), overridden["RadiusButton"]);
        Assert.Equal(ThemeColors.ParseColor(palette.AppBackground),
            ((SolidColorBrush)overridden["AppBackground"]).Color);

        // An unknown theme id falls back to sharp-dark values end to end.
        var fallback = new ResourceDictionary();
        ThemeResourceApplier.Apply(fallback, new ThemeSettings { ThemeId = "no-such-theme" }, new PlayerSettings());
        Assert.Equal(ThemeColors.ParseColor(ThemeCatalog.PresetFor("sharp-dark").Palette.AppBackground),
            ((SolidColorBrush)fallback["AppBackground"]).Color);
        Assert.Equal(new CornerRadius(ThemeCatalog.PresetFor("sharp-dark").Radii.Button), fallback["RadiusButton"]);
    });

    [Fact]
    public void ThemeResourceApplier_derives_and_applies_the_accent_state_tokens_REQ_UI_01() => StaTestThread.Invoke(() =>
    {
        // Phase B / Task 4: the applier derives the accent state set for the resolved (accent x theme)
        // and replaces every token + its Color companion, so DynamicResource consumers re-resolve.
        // Uses dim steel on soft-glass — the CON-1 case whose pressed foreground must flip to white.
        var res = new ResourceDictionary();
        ThemeResourceApplier.Apply(res, new ThemeSettings { ThemeId = "soft-glass", AccentColor = "#4A8FAB" }, new PlayerSettings());

        var expected = ThemeColors.DeriveAccentSet("#4A8FAB", ThemeCatalog.PresetFor("soft-glass"));
        void AssertToken(string key, Color color)
        {
            var brush = Assert.IsType<SolidColorBrush>(res[key]);
            Assert.True(brush.IsFrozen, $"{key} brush not frozen.");
            Assert.Equal(color, brush.Color);
            Assert.Equal(color, (Color)res[key + "Color"]);   // companion Color in step
        }
        AssertToken("AccentPrimary", expected.Primary);
        AssertToken("OnAccent", expected.OnAccent);
        AssertToken("AccentHover", expected.Hover);
        AssertToken("AccentPressed", expected.Pressed);
        AssertToken("OnAccentPressed", expected.OnAccentPressed);
        AssertToken("AccentBorder", expected.Border);
        AssertToken("AccentShellTint", expected.ShellTint);
        AssertToken("AccentChromeGlyph", expected.ChromeGlyph);

        // CON-1 remains a contrast contract even when the presentation fill itself is lifted.
        Assert.True(ThemeColors.ContrastRatio(expected.OnAccentPressed, expected.Pressed) >= 4.5);

        // AccentPrimaryLight is kept as an alias to AccentHover for one migration pass.
        Assert.Equal(expected.Hover, ((SolidColorBrush)res["AccentPrimaryLight"]).Color);
    });

    [Fact]
    public void ThemeResourceApplier_applies_density_and_border_tokens_from_the_preset() => StaTestThread.Invoke(() =>
    {
        // Task 5: the applier replaces the density doubles + padding/border Thicknesses from the
        // preset's ThemeDensity, so DynamicResource consumers re-resolve. Soft Glass is the airy end.
        var res = new ResourceDictionary();
        ThemeResourceApplier.Apply(res, new ThemeSettings { ThemeId = "soft-glass" }, new PlayerSettings());

        var density = ThemeCatalog.PresetFor("soft-glass").Density;
        Assert.Equal(density.ControlHeight, (double)res["DensityControlHeight"]);
        Assert.Equal(density.IconButtonSize, (double)res["DensityIconButtonSize"]);
        Assert.Equal(density.ScrollbarThickness, (double)res["DensityScrollbarThickness"]);
        Assert.Equal(density.ButtonPadding, (Thickness)res["DensityButtonPadding"]);
        Assert.Equal(density.InputPadding, (Thickness)res["DensityInputPadding"]);
        Assert.Equal(density.MenuItemPadding, (Thickness)res["DensityMenuItemPadding"]);
        Assert.Equal(density.PresetChipPadding, (Thickness)res["DensityPresetChipPadding"]);
        Assert.Equal(density.ToolTipPadding, (Thickness)res["DensityToolTipPadding"]);
        // BorderThicknessDefault resolves as a Thickness struct (a double/string would crash the
        // .NET 10 DynamicResource BorderThickness consumer).
        Assert.Equal(density.BorderThicknessDefault, (Thickness)res["BorderThicknessDefault"]);

        // An unknown theme id falls back to sharp-dark density end to end (the compact profile).
        var fallback = new ResourceDictionary();
        ThemeResourceApplier.Apply(fallback, new ThemeSettings { ThemeId = "no-such-theme" }, new PlayerSettings());
        Assert.Equal(ThemeCatalog.PresetFor("sharp-dark").Density.ControlHeight, (double)fallback["DensityControlHeight"]);
        Assert.Equal(ThemeCatalog.PresetFor("sharp-dark").Density.ButtonPadding, (Thickness)fallback["DensityButtonPadding"]);
    });

    [Fact]
    public void ThemeResourceApplier_sets_inner_elevation_effects_and_clears_them_for_sharp() => StaTestThread.Invoke(() =>
    {
        // Soft Glass gets frozen DropShadowEffects whose blur/depth/opacity match the preset's
        // ThemeElevation (ElevationPopup feeds popups; ElevationPanel raised inner panels).
        var res = new ResourceDictionary();
        ThemeResourceApplier.Apply(res, new ThemeSettings { ThemeId = "soft-glass" }, new PlayerSettings());

        var elevation = ThemeCatalog.PresetFor("soft-glass").Elevation!;
        var popup = Assert.IsType<DropShadowEffect>(res["ElevationPopup"]);
        Assert.True(popup.IsFrozen, "ElevationPopup effect must be frozen (shareable across windows).");
        Assert.Equal(elevation.PopupBlurRadius, popup.BlurRadius);
        Assert.Equal(elevation.PopupShadowDepth, popup.ShadowDepth);
        Assert.Equal(elevation.PopupOpacity, popup.Opacity);
        var panel = Assert.IsType<DropShadowEffect>(res["ElevationPanel"]);
        Assert.True(panel.IsFrozen);
        Assert.Equal(elevation.PanelBlurRadius, panel.BlurRadius);
        Assert.Equal(elevation.PanelShadowDepth, panel.ShadowDepth);
        Assert.Equal(elevation.PanelOpacity, panel.Opacity);

        // Switching to Sharp Dark CLEARS the inner shadow: the applier overwrites the prior effect with
        // a literal null (Sharp is flat; a no-op DropShadowEffect would still cost per-frame raster).
        // Re-applying onto the same dict proves the null write actively lands — stronger than a
        // Contains() check on an empty dictionary, and it mirrors a real soft-glass -> sharp switch.
        ThemeResourceApplier.Apply(res, new ThemeSettings { ThemeId = "sharp-dark" }, new PlayerSettings());
        Assert.Null(res["ElevationPopup"]);
        Assert.Null(res["ElevationPanel"]);
    });

    [Fact]
    public void ComboBox_dropdown_consumes_the_inner_elevation_token() => StaTestThread.Invoke(() =>
    {
        // Task 7: the ComboBox dropdown surface (DropDownBorder, hosted in a real Popup HWND) carries the
        // inner elevation via {DynamicResource ElevationPopup} — a frozen DropShadowEffect for Soft Glass /
        // Minimal, and a null Effect (flat) for Sharp. Replacing the app token restyles the realized
        // border, the same replace-not-mutate path proven for palette/radius/density.
        var original = Application.Current.Resources["ElevationPopup"];
        try
        {
            var sentinel = new DropShadowEffect { BlurRadius = 17, ShadowDepth = 2, Opacity = 0.3, Color = Colors.Black };
            sentinel.Freeze();
            Application.Current.Resources["ElevationPopup"] = sentinel;

            var combo = new ComboBox { Style = (Style)Application.Current.FindResource("DarkComboBox") };
            combo.Measure(new Size(200, 32));
            combo.ApplyTemplate();
            var dropDownBorder = (Border)combo.Template.FindName("DropDownBorder", combo)!;
            Assert.Same(sentinel, dropDownBorder.Effect);

            // Sharp Dark resolves ElevationPopup to null → the dropdown is flat (no inner shadow).
            Application.Current.Resources["ElevationPopup"] = null;
            var sharpCombo = new ComboBox { Style = (Style)Application.Current.FindResource("DarkComboBox") };
            sharpCombo.Measure(new Size(200, 32));
            sharpCombo.ApplyTemplate();
            var sharpBorder = (Border)sharpCombo.Template.FindName("DropDownBorder", sharpCombo)!;
            Assert.Null(sharpBorder.Effect);
        }
        finally
        {
            Application.Current.Resources["ElevationPopup"] = original;   // never pollute the shared app
        }
    });

    [Fact]
    public void Profile_combo_keeps_a_neutral_outer_frame_REQ_UI_01() => StaTestThread.Invoke(() =>
    {
        var combo = new ComboBox
        {
            Style = (Style)Application.Current.FindResource("DarkComboBox"),
            ItemsSource = new[]
            {
                new Profile
                {
                    Name = "Violet",
                    Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                    AccentColor = "#A78BFA",
                },
            },
            SelectedIndex = 0,
        };

        combo.Measure(new Size(200, 32));
        combo.Arrange(new Rect(0, 0, 200, 32));
        combo.ApplyTemplate();
        var toggle = (ToggleButton)combo.Template.FindName("ToggleButton", combo)!;
        toggle.ApplyTemplate();
        var frame = (Border)toggle.Template.FindName("bd", toggle)!;

        var brush = Assert.IsType<SolidColorBrush>(frame.BorderBrush);
        Assert.Equal(Colors.Transparent, brush.Color);
    });

    [Fact]
    public void Profile_identity_marker_keeps_valid_dark_color_visible_REQ_PROFILE_01() => StaTestThread.Invoke(() =>
    {
        var originalSurface = Application.Current.Resources["SurfaceHover"];
        var preset = ThemeCatalog.PresetFor("sharp-dark");
        try
        {
            var initialSurface = new SolidColorBrush(ThemeColors.ParseColor(preset.Palette.SurfaceHover));
            initialSurface.Freeze();
            Application.Current.Resources["SurfaceHover"] = initialSurface;

            var window = new MainWindow();
            var combo = (ComboBox)window.FindName("ProfilesCombo")!;
            combo.ItemsSource = new[]
            {
                new Profile
                {
                    Name = "Dark identity",
                    Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                    AccentColor = preset.Palette.SurfaceRaised,
                },
            };
            combo.SelectedIndex = 0;
            combo.Measure(new Size(150, 32));
            combo.Arrange(new Rect(0, 0, 150, 32));
            combo.ApplyTemplate();
            combo.UpdateLayout();

            var content = (ContentPresenter)combo.Template.FindName("ContentSite", combo)!;
            content.ApplyTemplate();
            var rail = Assert.IsType<Border>(FindVisualChild(content, "ProfileIdentityColorRail"));
            rail.GetBindingExpression(Border.BackgroundProperty)?.UpdateTarget();
            var first = Assert.IsType<SolidColorBrush>(rail.Background).Color;
            var ratio = ThemeColors.ContrastRatio(first, initialSurface.Color);
            Assert.True(ratio >= 3.0, $"Profile identity rail contrast is only {ratio:F2}:1.");

            // The rail carries SurfaceHover through a DynamicResource-backed Tag into its
            // MultiBinding, so an already-realized marker must reconvert on a live theme surface swap.
            var replacementSurface = new SolidColorBrush(Color.FromRgb(0x3A, 0x43, 0x50));
            replacementSurface.Freeze();
            Application.Current.Resources["SurfaceHover"] = replacementSurface;
            rail.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            var updated = Assert.IsType<SolidColorBrush>(rail.Background).Color;
            Assert.NotEqual(first, updated);
            Assert.True(ThemeColors.ContrastRatio(updated, replacementSurface.Color) >= 3.0);

            combo.ItemsSource = new[]
            {
                new Profile { Name = "Plain", Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" },
            };
            combo.SelectedIndex = 0;
            combo.UpdateLayout();
            content.ApplyTemplate();
            rail = Assert.IsType<Border>(FindVisualChild(content, "ProfileIdentityColorRail"));
            rail.GetBindingExpression(Border.BackgroundProperty)?.UpdateTarget();
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(rail.Background).Color);
        }
        finally
        {
            Application.Current.Resources["SurfaceHover"] = originalSurface;
        }
    });

    [Fact]
    public void Theme_restyle_reaches_dynamic_surface_and_radius_consumers() => StaTestThread.Invoke(() =>
    {
        // The PR #18 replace-not-mutate mechanism, applied verbatim to the new tokens: a DarkButton
        // resolves {DynamicResource SurfaceRaised} (fill) and {DynamicResource RadiusButton}
        // (template corner), so REPLACING those app entries restyles consumers at realize time.
        var originalSurface = Application.Current.Resources["SurfaceRaised"];
        var originalRadius = Application.Current.Resources["RadiusButton"];
        try
        {
            var sentinel = Color.FromRgb(0x12, 0x34, 0x56);
            var brush = new SolidColorBrush(sentinel);
            brush.Freeze();
            Application.Current.Resources["SurfaceRaised"] = brush;
            Application.Current.Resources["RadiusButton"] = new CornerRadius(13);

            var btn = new Button { Style = (Style)Application.Current.FindResource("DarkButton") };
            btn.Measure(new Size(200, 40));   // realize: style + template resolve the replaced entries
            Assert.Equal(sentinel, ((SolidColorBrush)btn.Background).Color);
            var bd = (Border)btn.Template.FindName("bd", btn);
            Assert.Equal(new CornerRadius(13), bd.CornerRadius);
        }
        finally
        {
            Application.Current.Resources["SurfaceRaised"] = originalSurface;   // never pollute the shared app
            Application.Current.Resources["RadiusButton"] = originalRadius;
        }
    });

    [Fact]
    public void Theme_restyle_reaches_dynamic_density_consumers() => StaTestThread.Invoke(() =>
    {
        // TG-8: realized button/input styles re-resolve the new density/border tokens via
        // DynamicResource — the same replace-not-mutate mechanism proven for surface/radius above, now
        // for Padding/BorderThickness/MinHeight. Replacing the app entries restyles realized consumers.
        var originalPad = Application.Current.Resources["DensityButtonPadding"];
        var originalBorder = Application.Current.Resources["BorderThicknessDefault"];
        var originalHeight = Application.Current.Resources["DensityControlHeight"];
        try
        {
            Application.Current.Resources["DensityButtonPadding"] = new Thickness(7, 3, 7, 3);
            Application.Current.Resources["BorderThicknessDefault"] = new Thickness(4);
            Application.Current.Resources["DensityControlHeight"] = 41.0;

            var btn = new Button { Style = (Style)Application.Current.FindResource("DarkButton") };
            btn.Measure(new Size(200, 60));   // realize: style setters resolve the replaced entries
            Assert.Equal(new Thickness(7, 3, 7, 3), btn.Padding);
            Assert.Equal(new Thickness(4), btn.BorderThickness);

            var accent = new Button { Style = (Style)Application.Current.FindResource("AccentButton") };
            accent.Measure(new Size(200, 60));
            Assert.Equal(new Thickness(4), accent.BorderThickness);

            var box = new TextBox { Style = (Style)Application.Current.FindResource("DarkTextBox") };
            box.Measure(new Size(200, 60));
            Assert.Equal(41.0, box.MinHeight);
        }
        finally
        {
            Application.Current.Resources["DensityButtonPadding"] = originalPad;   // never pollute the shared app
            Application.Current.Resources["BorderThicknessDefault"] = originalBorder;
            Application.Current.Resources["DensityControlHeight"] = originalHeight;
        }
    });

    [Fact]
    public void Accent_recolor_fills_accent_button() => StaTestThread.Invoke(() =>
    {
        // The AccentButton wears the accent as its fill. REPLACING the App resource changes
        // what the consumer resolves — the recolor mechanism the compiled-BAML frozen seed brushes
        // cannot satisfy by mutation. (Resolution asserted at realize-time for this untethered button.)
        var original = Application.Current.Resources["AccentPrimary"];
        try
        {
            var sentinel = Color.FromRgb(0x12, 0x34, 0x56);
            var brush = new SolidColorBrush(sentinel);
            brush.Freeze();
            Application.Current.Resources["AccentPrimary"] = brush;

            var btn = new Button { Style = (Style)Application.Current.FindResource("AccentButton") };
            btn.Measure(new Size(200, 40));   // realize: Background resolves the replaced resource
            Assert.Equal(sentinel, ((SolidColorBrush)btn.Background).Color);
        }
        finally
        {
            Application.Current.Resources["AccentPrimary"] = original;   // never pollute the shared app
        }
    });

    [Fact]
    public void AccentButton_foreground_resolves_the_on_accent_token() => StaTestThread.Invoke(() =>
    {
        var original = Application.Current.Resources["OnAccent"];
        try
        {
            var sentinel = Color.FromRgb(0xAB, 0xCD, 0xEF);
            var brush = new SolidColorBrush(sentinel);
            brush.Freeze();
            Application.Current.Resources["OnAccent"] = brush;

            var btn = new Button { Style = (Style)Application.Current.FindResource("AccentButton") };
            btn.Measure(new Size(200, 40));   // realize: Foreground resolves the replaced resource
            Assert.Equal(sentinel, ((SolidColorBrush)btn.Foreground).Color);
        }
        finally
        {
            Application.Current.Resources["OnAccent"] = original;   // never pollute the shared app
        }
    });

    [Fact]
    public void PopOutButton_nested_text_uses_pixel_aligned_rendering() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        var label = (TextBlock)w.FindName("PopOutButtonText")!;
        var icon = (TextBlock)w.FindName("PopOutButtonIcon")!;

        foreach (var text in new[] { label, icon })
        {
            Assert.Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(text));
            Assert.Equal(TextHintingMode.Fixed, TextOptions.GetTextHintingMode(text));
            Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(text));
            Assert.Equal(((Button)w.FindName("PopOutButton")!).Foreground, text.Foreground);
        }
    });

    [Fact]
    public void SettingsWindow_shows_the_tested_privacy_wording() => StaTestThread.Invoke(() =>
    {
        var w = new SettingsWindow(isBrowserReady: true);
        Assert.Equal(PiPlay.Services.PrivacyService.ResetDescription,
            ((TextBlock)w.FindName("ResetDescriptionText")!).Text);
        Assert.Equal(PiPlay.Services.PrivacyService.ClearDescription,
            ((TextBlock)w.FindName("ClearDescriptionText")!).Text);
        Assert.Equal(PiPlay.Services.PrivacyService.ResetActionLabel,
            (string)((Button)w.FindName("ResetAppStateButton")!).Content);
        Assert.Equal(PiPlay.Services.PrivacyService.ClearActionLabel,
            (string)((Button)w.FindName("ClearBrowserDataButton")!).Content);
    });

    [Fact]
    public void SettingsWindow_disables_only_clear_when_browser_not_ready() => StaTestThread.Invoke(() =>
    {
        var notReady = new SettingsWindow(isBrowserReady: false);
        Assert.True(((Button)notReady.FindName("ResetAppStateButton")!).IsEnabled);
        Assert.False(((Button)notReady.FindName("ClearBrowserDataButton")!).IsEnabled);

        var ready = new SettingsWindow(isBrowserReady: true);
        Assert.True(((Button)ready.FindName("ResetAppStateButton")!).IsEnabled);
        Assert.True(((Button)ready.FindName("ClearBrowserDataButton")!).IsEnabled);
    });

    [Fact]
    public void SettingsWindow_explains_why_clear_is_disabled() => StaTestThread.Invoke(() =>
    {
        var notReady = new SettingsWindow(isBrowserReady: false);
        var clear = (Button)notReady.FindName("ClearBrowserDataButton")!;
        Assert.Equal(PiPlay.Services.PrivacyService.ClearNotReadyHint, (string)clear.ToolTip);
        Assert.True(ToolTipService.GetShowOnDisabled(clear));   // tip shows on the disabled button

        // When the browser is ready the button is enabled and carries no explanatory tooltip.
        var ready = new SettingsWindow(isBrowserReady: true);
        Assert.Null(((Button)ready.FindName("ClearBrowserDataButton")!).ToolTip);
    });

    [Fact]
    public void SettingsWindow_explains_when_a_previous_clear_is_still_running() => StaTestThread.Invoke(() =>
    {
        var dialog = new SettingsWindow(
            isBrowserReady: true,
            clearBrowserDataUnavailableHint: PrivacyService.ClearAlreadyRunningHint);
        var clear = (Button)dialog.FindName("ClearBrowserDataButton")!;

        Assert.False(clear.IsEnabled);
        Assert.Equal(PrivacyService.ClearAlreadyRunningHint, clear.ToolTip);
        Assert.True(ToolTipService.GetShowOnDisabled(clear));

        dialog.SetClearBrowserDataAvailability(isBrowserReady: true);
        Assert.True(clear.IsEnabled);
        Assert.Null(clear.ToolTip);

        dialog.SetClearBrowserDataAvailability(isBrowserReady: false);
        Assert.False(clear.IsEnabled);
        Assert.Equal(PrivacyService.ClearNotReadyHint, clear.ToolTip);
    });

    // --- Resolved DependencyProperty invariants (runtime counterpart to Layer 1) ---

    [Fact]
    public void MainWindow_holds_layout_and_airspace_invariants() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        Assert.False(w.UseLayoutRounding);
        Assert.False(w.AllowsTransparency);
        Assert.Equal(WindowStyle.None, w.WindowStyle);
        var chrome = WindowChrome.GetWindowChrome(w);
        Assert.NotNull(chrome);
        Assert.Equal(new CornerRadius(0), chrome!.CornerRadius);
    });

    [Fact]
    public void PlayerWindow_holds_layout_and_airspace_invariants() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        Assert.False(w.UseLayoutRounding);
        Assert.False(w.AllowsTransparency);
        Assert.Equal(WindowStyle.None, w.WindowStyle);
        Assert.Equal(new CornerRadius(0), WindowChrome.GetWindowChrome(w)!.CornerRadius);
    });

    [Fact]
    public void Expanded_resize_hook_returns_diagonal_corner_for_real_nchittest_message() => StaTestThread.Invoke(() =>
    {
        var w = new Window
        {
            Width = 240,
            Height = 160,
            Left = 100,
            Top = 100,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.CanResize,
            AllowsTransparency = false,
            Opacity = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
        };

        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(BorderlessResizeHitTestPolicy.ResizeBorderDip),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        BorderlessWindowHelper.EnableExpandedResizeZones(w);

        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        w.Show();
        w.UpdateLayout();
        Assert.True(BorderlessWindowHelper.HasExpandedResizeSubclassForTests(hwnd));
        // Probe a point inside the top resize band (y < ResizeBorderDip) and within the configured
        // left corner length — policy-relative so owner tuning needs no recalibration.
        var probeY = BorderlessResizeHitTestPolicy.ResizeBorderDip / 2;
        const double probeX = 20;
        var p = w.PointToScreen(new Point(probeX, probeY));
        var roundTrip = w.PointFromScreen(p);
        Assert.InRange(roundTrip.X, probeX - 0.5, probeX + 0.5);
        Assert.InRange(roundTrip.Y, probeY - 0.5, probeY + 0.5);
        var result = SendMessage(hwnd, WM_NCHITTEST, IntPtr.Zero, MakeLParam((int)p.X, (int)p.Y));

        Assert.Equal(BorderlessResizeHitTestPolicy.HTTOPLEFT, result.ToInt32());
    });

    [Fact]
    public void MainWindow_exposes_settings_button() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        Assert.IsType<Button>(w.FindName("SettingsButton"));
    });

    [Fact]
    public void MainWindow_source_pin_uses_the_theme_accent() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        // Inject default settings so the assertion is isolated from any accent persisted to the shared
        // test data root by an earlier test (the default WAS cyan; it is now the deepened #2BAED0).
        w.ReplaceSettingsForTests(new AppSettings());
        var pin = (ToggleButton)w.FindName("PinToggle")!;
        var hint = (TextBlock)w.FindName("PinnedHint")!;

        // The source Pin glyph and the pinned hint share one accent brush built from Theme.AccentColor
        // (overhaul Task 10); default settings resolve to the sharp-dark deep cyan.
        var accent = (SolidColorBrush)ToggleAccent.GetCheckedBrush(pin)!;
        Assert.Equal(ThemeColors.ParseColor(ThemeCatalog.DefaultAccentColor), accent.Color);
        Assert.Equal(accent.Color, ((SolidColorBrush)hint.Foreground).Color);
    });

    [Fact]
    public void MainWindow_dark_accent_uses_safe_primary_for_pin_and_hint_REQ_UI_01() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        w.ReplaceSettingsForTests(new AppSettings
        {
            Theme = new ThemeSettings { ThemeId = "sharp-dark", AccentColor = "#131820" },
        });
        var pin = (ToggleButton)w.FindName("PinToggle")!;
        var hint = (TextBlock)w.FindName("PinnedHint")!;
        var expected = ((SolidColorBrush)Application.Current.Resources["AccentPrimary"]).Color;

        Assert.Equal(expected, ((SolidColorBrush)ToggleAccent.GetCheckedBrush(pin)!).Color);
        Assert.Equal(expected, ((SolidColorBrush)hint.Foreground).Color);
        Assert.True(ThemeColors.ContrastRatio(
            expected, ThemeColors.ParseColor(ThemeCatalog.PresetFor("sharp-dark").Palette.SurfaceHover)) >= 3.0);
    });

    [Fact]
    public void MainWindow_resolves_popout_preferences_from_theme_overrides() => StaTestThread.Invoke(() =>
    {
        // Regression for PR #18 review: the ThemePreferenceResolver tests were correct, but
        // MainWindow still fed new/reapplied popouts from the stale legacy PlayerSettings fields.
        var w = new MainWindow();
        w.ReplaceSettingsForTests(new AppSettings
        {
            Player = new PlayerSettings
            {
                PinAccent = "amber",
                FadeIdleDelayMs = 4000,
                ConstantWindowOpacity = 0.9,
                IdleWindowOpacity = 0.7,
                StripAutoHide = true,
            },
            Theme = new ThemeSettings
            {
                AccentColor = "#A78BFA",
                FadeDelayPreset = "short",
                ActiveWindowOpacity = 0.6,
                IdleWindowOpacity = 0.4,
                StripAutoHide = false,
                CornerStyle = "round",
            },
        });

        var prefs = w.EffectivePlayerPreferencesForTests;

        Assert.Equal("#A78BFA", prefs.AccentColor);
        Assert.Equal(1500, prefs.FadeIdleDelayMs);
        Assert.Equal(0.6, prefs.ActiveWindowOpacity);
        Assert.Equal(0.4, prefs.IdleWindowOpacity);
        Assert.False(prefs.StripAutoHide);
        Assert.Equal("round", prefs.CornerStyle);
    });

    [Fact]
    public void PlayerWindow_applies_one_accent_to_pin_and_fade_and_the_delay_REQ_UI_01() => StaTestThread.Invoke(() =>
    {
        var w = new PlayerWindow(
            environment: null!,
            url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false,
            placement: null,
            defaultWidth: 960,
            defaultHeight: 540,
            fadeEnabled: true,
            accentColor: "#38D996",
            fadeIdleDelayMs: 4000);

        var pin = (ToggleButton)w.FindName("PinToggle")!;
        var fade = (ToggleButton)w.FindName("FadeToggle")!;

        // One accent brush drives BOTH toggles (overhaul Task 10): the same frozen instance, colored
        // from Theme.AccentColor.
        Assert.Same(ToggleAccent.GetCheckedBrush(pin), ToggleAccent.GetCheckedBrush(fade));
        Assert.Equal(Color.FromRgb(0x38, 0xD9, 0x96), ((SolidColorBrush)ToggleAccent.GetCheckedBrush(pin)!).Color);

        w.ApplyAppearance("#A78BFA", 1500);
        Assert.Equal(Color.FromRgb(0xA7, 0x8B, 0xFA), ((SolidColorBrush)ToggleAccent.GetCheckedBrush(pin)!).Color);
        Assert.Equal(Color.FromRgb(0xA7, 0x8B, 0xFA), ((SolidColorBrush)ToggleAccent.GetCheckedBrush(fade)!).Color);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), w.FadeIdleDelayForTests);

        w.ApplyAppearance("#131820", 1500);
        var surface = ((SolidColorBrush)Application.Current.Resources["SurfaceHover"]).Color;
        var darkDisplay = ((SolidColorBrush)ToggleAccent.GetCheckedBrush(pin)!).Color;
        Assert.True(ThemeColors.ContrastRatio(darkDisplay, surface) >= 3.0);
        Assert.Equal(darkDisplay, ((SolidColorBrush)ToggleAccent.GetCheckedBrush(fade)!).Color);
    });

    [Fact]
    public void MainWindow_active_opacity_reaches_only_the_source_title_bar_background() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        w.ReplaceSettingsForTests(new AppSettings
        {
            Theme = new ThemeSettings { ActiveWindowOpacity = 0.63 },
        });

        var backdrop = (Border)w.FindName("MainBarBackdrop")!;
        var title = (TextBlock)w.FindName("TitleText")!;
        Assert.Equal(0.63, backdrop.Opacity);
        Assert.Equal(1.0, title.Opacity);   // text/buttons do not inherit the background fade
        Assert.Equal(1.0, w.Opacity);       // browser/window stay opaque
    });

    [Fact]
    public void MainWindow_full_theme_preview_and_cancel_restore_palette_and_bar_opacity() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        w.ReplaceSettingsForTests(new AppSettings
        {
            Theme = new ThemeSettings { ThemeId = "sharp-dark" },
        });
        var preview = new SettingsWindow(isBrowserReady: true, themeId: "soft-glass");

        w.PreviewThemeForTests(preview);
        Assert.Equal(ThemeColors.ParseColor("#0B1018"),
            ((SolidColorBrush)Application.Current.Resources["AppBackground"]).Color);
        Assert.Equal(0.82, ((Border)w.FindName("MainBarBackdrop")!).Opacity);

        w.RevertAppearancePreviewForTests();
        Assert.Equal(ThemeColors.ParseColor("#050609"),
            ((SolidColorBrush)Application.Current.Resources["AppBackground"]).Color);
        Assert.Equal(1.0, ((Border)w.FindName("MainBarBackdrop")!).Opacity);
    });

    /// <summary>
    /// Cancelling Settings must undo the WHOLE preview transaction, and a palette check alone does not
    /// prove that: RevertAppearancePreview's ApplyThemeResources repaints the accent from the GLOBAL
    /// Theme.AccentColor, so with a colored profile active it is the FOLLOWING ApplyResolvedAccent that
    /// puts the profile's color back. Drop that one line and dismissing Settings leaves the app wearing
    /// the wrong color — a mutation the palette-only guard could not see. The open popout is the other
    /// half: nothing else can show that the player was left on the previewed appearance.
    /// </summary>
    [Fact]
    public void MainWindow_cancel_restores_the_profile_accent_and_the_open_player_REQ_UI_01() => StaTestThread.Invoke(() =>
    {
        var settings = new AppSettings
        {
            ActiveProfileName = "Violet",
            Theme = new ThemeSettings
            {
                ThemeId = "sharp-dark",
                AccentColor = "#00D4FF",       // the GLOBAL accent: what a half-done revert would paint
                AccentIntensity = 50,
                // Deliberately NOT the dialog's default ("normal" = 2500ms): a persisted value the
                // preview cannot coincidentally restore, so the fade assertion below discriminates.
                FadeDelayPreset = "long",      // 4000ms
            },
            Profiles =
            {
                new Profile
                {
                    Name = "Violet",
                    Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                    AccentColor = "#A78BFA",   // the accent actually on screen
                },
            },
        };

        var w = new MainWindow();
        w.ReplaceSettingsForTests(settings);
        w.SelectProfileForTests("Violet");

        var player = new PlayerWindow(
            environment: null!,
            url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false,
            placement: null,
            defaultWidth: 960,
            defaultHeight: 540,
            fadeEnabled: true,
            accentColor: "#A78BFA",
            fadeIdleDelayMs: 4000);
        w.AttachPlayerForTests(player);

        // Preview a different preset AND a different reach, exactly as dragging in Settings would.
        var preview = new SettingsWindow(isBrowserReady: true, themeId: "soft-glass", accentIntensity: 100);
        w.PreviewThemeForTests(preview);

        // The preview really did reach the open popout — otherwise the revert below proves nothing.
        Assert.Equal((0.82, 0.72), player.WindowOpacityLevelsForTests);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), player.FadeIdleDelayForTests);

        w.RevertAppearancePreviewForTests();

        // The accent must return to the PROFILE's color at the PERSISTED reach — not the global accent,
        // and not the previewed reach.
        var expected = ThemeColors.DeriveAccentSet("#A78BFA", ThemeCatalog.PresetFor("sharp-dark"), 50);
        Assert.Equal(expected.Primary, ((SolidColorBrush)Application.Current.Resources["AccentPrimary"]).Color);
        Assert.Equal(expected.ChromeGlyph, ((SolidColorBrush)Application.Current.Resources["AccentChromeGlyph"]).Color);
        Assert.Equal("#A78BFA", w.ResolvedAccentColorForTests);
        Assert.Equal(50, w.EffectiveAccentIntensityForTests);

        // ...and the open popout must be back on the persisted appearance, not the previewed one.
        Assert.Equal((1.0, 1.0), player.WindowOpacityLevelsForTests);
        Assert.Equal(TimeSpan.FromMilliseconds(4000), player.FadeIdleDelayForTests);

        w.AttachPlayerForTests(null);
        player.Close();
    });

    [Fact]
    public void PlayerWindow_settings_button_raises_one_request_without_owning_settings() => StaTestThread.Invoke(() =>
    {
        var w = new PlayerWindow(
            environment: null!,
            url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false,
            placement: null,
            defaultWidth: 960,
            defaultHeight: 540,
            fadeEnabled: true);
        var requests = 0;
        w.SettingsRequested += (_, _) => requests++;

        ((Button)w.FindName("SettingsButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(1, requests);
    });

    [Fact]
    public void Existing_settings_dialog_is_raised_above_a_pinned_repeat_requester() => StaTestThread.Invoke(() =>
    {
        var source = new MainWindow();
        var dialog = new SettingsWindow(isBrowserReady: true);
        var pinnedRequester = new Window { Topmost = true };

        source.PrepareExistingSettingsDialogForTests(dialog, pinnedRequester);

        Assert.True(dialog.Topmost);
    });

    [Fact]
    public void PlayerWindow_accent_only_update_preserves_behavior_settings() => StaTestThread.Invoke(() =>
    {
        var w = new PlayerWindow(
            environment: null!,
            url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false,
            placement: null,
            defaultWidth: 960,
            defaultHeight: 540,
            fadeEnabled: true,
            accentColor: "#38D996",
            fadeIdleDelayMs: 4000,
            constantWindowOpacity: 0.72,
            idleWindowOpacity: 0.48,
            stripAutoHide: true);

        w.ApplyAccent("#A78BFA");

        var pin = (ToggleButton)w.FindName("PinToggle")!;
        var fade = (ToggleButton)w.FindName("FadeToggle")!;
        Assert.Equal(Color.FromRgb(0xA7, 0x8B, 0xFA), ((SolidColorBrush)ToggleAccent.GetCheckedBrush(pin)!).Color);
        Assert.Same(ToggleAccent.GetCheckedBrush(pin), ToggleAccent.GetCheckedBrush(fade));
        Assert.Equal(TimeSpan.FromMilliseconds(4000), w.FadeIdleDelayForTests);
        Assert.Equal((0.72, 0.48), w.WindowOpacityLevelsForTests);
        Assert.True(w.StripAutoHideForTests);
    });

    [Fact]
    public void PlayerWindow_sync_poll_is_generation_bound_single_flight_and_success_gated() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();

        var failedGeneration = w.BeginNavigationForTests();
        Assert.False(w.IsSyncTimerRunningForTests);
        Assert.True(w.CompleteNavigationForTests(failedGeneration, succeeded: false));
        Assert.False(w.IsSyncTimerRunningForTests);   // failed normal navigation never polls
        Assert.False(w.TryBeginSyncPollForTests(out _));

        var readyGeneration = w.BeginNavigationForTests();
        Assert.True(w.CompleteNavigationForTests(readyGeneration, succeeded: true));
        Assert.True(w.IsSyncTimerRunningForTests);
        Assert.True(w.TryBeginSyncPollForTests(out var pollGeneration));
        Assert.False(w.TryBeginSyncPollForTests(out _));   // the async tick is single-flight

        var replacementGeneration = w.BeginNavigationForTests();
        Assert.False(w.IsSyncTimerRunningForTests);        // navigation start stops polling
        Assert.False(w.IsSyncPollCurrentForTests(pollGeneration));
        Assert.False(w.CompleteNavigationForTests(readyGeneration, succeeded: true));
        Assert.False(w.IsSyncTimerRunningForTests);        // stale completion cannot restart it
        w.EndSyncPollForTests();

        Assert.True(w.CompleteNavigationForTests(replacementGeneration, succeeded: true));
        Assert.True(w.IsSyncTimerRunningForTests);
        w.Close();
        Assert.True(w.IsClosingForTests);
        Assert.False(w.IsSyncTimerRunningForTests);
        Assert.False(w.TryBeginSyncPollForTests(out _));
    });

    [Fact]
    public async Task Runtime_deadline_starts_a_contended_single_flight_factory_on_the_ui_thread()
    {
        using var gate = new SemaphoreSlim(0, 1);
        Task<string>? queued = null;
        var dispatcherThreadId = -1;
        var factoryThreadId = -1;

        StaTestThread.Invoke(() =>
        {
            dispatcherThreadId = Environment.CurrentManagedThreadId;
            queued = AsyncOperationDeadline.RunSingleFlightAsync(
                gate,
                () =>
                {
                    factoryThreadId = Environment.CurrentManagedThreadId;
                    return Task.FromResult("completed");
                },
                TimeSpan.FromSeconds(2));
        });

        gate.Release();
        Assert.Equal("completed", await queued!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(dispatcherThreadId, factoryThreadId);
    }

    [Fact]
    public void PlayerWindow_close_invalidates_an_in_flight_bridge_initialization() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        var generation = w.BeginPlayerInitializationForTests();

        Assert.True(w.IsPlayerInitializationCurrentForTests(generation));
        var replacementGeneration = w.BeginPlayerInitializationForTests();
        Assert.False(w.IsPlayerInitializationCurrentForTests(generation));
        Assert.True(w.IsPlayerInitializationCurrentForTests(replacementGeneration));
        w.Close();

        Assert.False(w.IsPlayerInitializationCurrentForTests(replacementGeneration));
    });

    [Fact]
    public void Reset_clears_dirty_ui_without_a_live_browser() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        var pin = (ToggleButton)w.FindName("PinToggle")!;
        var profiles = (ComboBox)w.FindName("ProfilesCombo")!;

        // Drive a dirty pre-state so the assertions prove the reset TRANSITION, not the
        // already-true fresh-construction defaults. (Setting IsChecked programmatically does not
        // fire the Click handler; setting ItemsSource leaves SelectedIndex at -1, so no navigation.)
        pin.IsChecked = true;
        profiles.ItemsSource = new[]
        {
            new Profile { Name = "Lo-fi", Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" },
        };
        Assert.NotEmpty(profiles.Items);
        Assert.True(pin.IsChecked);

        // CoreWebView2 is null because the window is never shown (Loaded never runs).
        var ex = Record.Exception(() => w.ApplyResetState());

        Assert.Null(ex);
        Assert.Empty(profiles.Items);                               // profiles cleared by reset
        Assert.False(pin.IsChecked);                                // pin turned off by reset
        Assert.Null(w.PendingUrlForTests);                          // reset queued no navigation
        Assert.Null(((WebView2)w.FindName("Browser")!).Source);     // browser source untouched
    });

    [Fact]
    public void Clear_is_not_ready_on_a_window_without_a_browser() => StaTestThread.Invoke(() =>
    {
        // The window is never shown, so the WebView2 core is never created: Clear must gate off.
        // Guards the execution-time readiness re-check that prevents clearing with a stale state.
        var w = new MainWindow();
        Assert.False(w.CanClearBrowserData);
    });

    [Fact]
    public void SettingsWindow_has_no_requested_action_until_confirmed() => StaTestThread.Invoke(() =>
    {
        var w = new SettingsWindow(isBrowserReady: true);
        Assert.Equal(PrivacyAction.None, w.RequestedAction);
    });

    [Fact]
    public void DangerButton_style_resolves_at_runtime() => StaTestThread.Invoke(() =>
    {
        // The destructive confirm resolves DangerButton from code (Prompt.AskConfirm), not XAML,
        // so the markup StaticResource sweep misses it — prove it resolves to a Style at runtime.
        Assert.IsType<Style>(Application.Current.TryFindResource("DangerButton"));
    });

    [Fact]
    public void Prompt_dialogs_are_borderless_dark() => StaTestThread.Invoke(() =>
    {
        // The themed dialogs are built in code (no XAML), so the markup suite can't guard them.
        // Lock the borderless-dark invariants so they can't silently regress to a light chrome.
        var win = Prompt.BuildShell(owner: null, title: "Test title", out var body);
        Assert.Equal(WindowStyle.None, win.WindowStyle);
        Assert.False(win.AllowsTransparency);
        Assert.False(win.UseLayoutRounding);   // the "rounding = 0" guard also covers code-built dialogs
        Assert.Equal(ResizeMode.NoResize, win.ResizeMode);
        Assert.False(win.ShowInTaskbar);
        Assert.Same(Application.Current.Resources["AppBackground"], win.Background);
        Assert.NotNull(body);

        // 1px border > dock panel > [draggable title bar with a CloseIconButton, body].
        var border = Assert.IsType<Border>(win.Content);
        var dock = Assert.IsType<DockPanel>(border.Child);
        var bar = Assert.IsType<Grid>(dock.Children[0]);
        var close = Assert.Single(bar.Children.OfType<Button>(),
            b => ReferenceEquals(b.Style, Application.Current.Resources["CloseIconButton"]));

        // Code-built and icon-only: the markup a11y sweep can't see it, so pin the UIA name here
        // (REQ-UI-02, overhaul Task 7).
        Assert.False(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(close)),
            "Prompt close button is missing an automation name.");
    });

    [Fact]
    public void Named_controls_resolve_to_expected_types() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        Assert.IsType<TextBox>(w.FindName("UrlBox"));
        Assert.IsType<Button>(w.FindName("PopOutButton"));
        Assert.IsType<Button>(w.FindName("ShowPopoutButton"));
        Assert.IsType<Button>(w.FindName("ProfileActionsButton"));
        Assert.IsType<ContextMenu>(w.FindName("ProfileActionsMenu"));
        Assert.IsType<MenuItem>(w.FindName("SaveProfileMenuItem"));
        Assert.IsType<Button>(w.FindName("PlaceholderShowPopoutButton"));
        Assert.IsType<Button>(w.FindName("PlaceholderBringBackButton"));
        Assert.IsType<ComboBox>(w.FindName("ProfilesCombo"));
        Assert.IsType<Border>(w.FindName("SourcePlaceholder"));
        Assert.IsType<Border>(w.FindName("RuntimeErrorPanel"));
    });

    [Fact]
    public void Popout_action_state_flips_label_tooltip_and_uia_name_together() => StaTestThread.Invoke(() =>
    {
        // Q-6 / REQ-UI-02 / P4: while a popout is open the primary action returns playback, and
        // the accessible name must flip in the same code path as the label.
        var w = new MainWindow();
        var btn = (Button)w.FindName("PopOutButton")!;
        var icon = (TextBlock)w.FindName("PopOutButtonIcon")!;
        var label = (TextBlock)w.FindName("PopOutButtonText")!;
        var show = (Button)w.FindName("ShowPopoutButton")!;

        w.ApplyPopoutActionState(hasPlayer: true);
        Assert.Equal("Bring video back", label.Text);
        Assert.Equal("\uE73F", icon.Text);
        Assert.Equal("Bring video back", System.Windows.Automation.AutomationProperties.GetName(btn));
        Assert.Contains("Return playback", (string)btn.ToolTip);
        Assert.Equal(Visibility.Visible, show.Visibility);

        w.ApplyPopoutActionState(hasPlayer: false);
        Assert.Equal("Pop out video", label.Text);
        Assert.Equal("\uE8A7", icon.Text);
        Assert.Equal("Pop out video", System.Windows.Automation.AutomationProperties.GetName(btn));
        Assert.Contains("Pop out", (string)btn.ToolTip);
        Assert.Equal(Visibility.Collapsed, show.Visibility);
    });

    [Fact]
    public void Compact_Source_toolbar_keeps_the_transfer_icon_and_hides_only_its_text() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        var icon = (TextBlock)w.FindName("PopOutButtonIcon")!;
        var label = (TextBlock)w.FindName("PopOutButtonText")!;

        w.ApplySourceToolbarLayout(800);
        Assert.Equal(Visibility.Collapsed, label.Visibility);
        Assert.Equal(new Thickness(0), icon.Margin);

        w.ApplySourceToolbarLayout(1180);
        Assert.Equal(Visibility.Visible, label.Visibility);
        Assert.Equal(new Thickness(0, 0, 8, 0), icon.Margin);
    });

    [Fact]
    public void Source_placeholder_bring_back_button_is_accessible_recovery_action() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        var button = (Button)w.FindName("PlaceholderBringBackButton")!;

        Assert.Same(Application.Current.FindResource("AccentButton"), button.Style);
        Assert.Equal("Bring video back", button.Content);
        Assert.Equal("Bring video back", System.Windows.Automation.AutomationProperties.GetName(button));
        Assert.Contains("Return playback", (string)button.ToolTip);

        w.ShowSourcePlaceholder(true);
        Assert.Equal(Visibility.Visible, button.Visibility);
    });

    [Fact]
    public void Source_placeholder_surfaces_and_clears_the_fallback_note() => StaTestThread.Invoke(() =>
    {
        // A FallbackReason (today: a malformed playlist link) rides the placeholder (Q-6) and must
        // be cleared with it so a stale note can't survive into the next popout.
        var w = new MainWindow();
        var note = (TextBlock)w.FindName("PlaceholderNoteText")!;

        w.ShowSourcePlaceholder(true, "That playlist link couldn't be read - popped out the current video.");
        Assert.Equal(Visibility.Visible, note.Visibility);
        Assert.Contains("playlist link", note.Text);

        w.ShowSourcePlaceholder(true);   // no reason this time
        Assert.Equal(Visibility.Collapsed, note.Visibility);
        Assert.Equal(string.Empty, note.Text);

        w.ShowSourcePlaceholder(false);
        Assert.Equal(Visibility.Collapsed, note.Visibility);
    });

    [Fact]
    public void Auto_toggle_reflects_loaded_setting_and_is_off_by_default() => StaTestThread.Invoke(() =>
    {
        // No settings file in the temp data root => AutoPopout defaults off => the toggle is unchecked.
        var auto = (ToggleButton)new MainWindow().FindName("AutoToggle")!;
        Assert.NotEqual(true, auto.IsChecked);
    });

    [Fact]
    public void DarkTextBox_template_applies_and_resolves_part_content_host() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        var url = (TextBox)w.FindName("UrlBox")!;
        url.Measure(new Size(400, 32));
        url.Arrange(new Rect(0, 0, 400, 32));
        url.ApplyTemplate();
        Assert.NotNull(url.Template);
        Assert.NotNull(url.Template.FindName("PART_ContentHost", url));
    });

    // --- Compact player mode (Phase 3, Stage 1): mode-specific minimums + settings/profile UI ---

    [Fact]
    public void PlayerWindow_uses_normal_minimum_size_by_default() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();   // no mode argument -> PlaybackMode.Normal
        Assert.Equal(PlaybackModePolicy.NormalMinWidth, w.MinWidth);
        Assert.Equal(PlaybackModePolicy.NormalMinHeight, w.MinHeight);
    });

    /// <summary>
    /// The accent-reach dial must survive a repaint that is not a Settings apply. Every accent repaint
    /// goes through ApplyAccentEverywhere — profile switch, dialog dismiss, reset — and ApplyAccentOnly
    /// DEFAULTS the intensity to the catalog value, so a repaint that forgets to pass the persisted dial
    /// still compiles and still looks wired while snapping a user who chose 0 back to 50.
    /// </summary>
    [Fact]
    public void MainWindow_repaints_with_the_persisted_accent_intensity_not_the_default() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        try
        {
            // Intensity 0 = the accent paints only the primary action: toolbar glyphs are ordinary text.
            w.ReplaceSettingsForTests(new AppSettings { Theme = new ThemeSettings { AccentIntensity = 0 } });

            var textPrimary = ThemeColors.ParseColor(ThemeCatalog.PresetFor("sharp-dark").Palette.TextPrimary);
            Assert.Equal(0, w.EffectiveAccentIntensityForTests);
            Assert.Equal(textPrimary, ((SolidColorBrush)Application.Current.Resources["AccentChromeGlyph"]).Color);
        }
        finally
        {
            w.ReplaceSettingsForTests(new AppSettings());   // restore App resources for other tests
        }
    });

    /// <summary>
    /// The intensity slider previews live, and the preview is VISUAL-ONLY: it must never write to
    /// settings, or dismissing Settings without applying could not revert it.
    /// </summary>
    [Fact]
    public void MainWindow_previews_accent_intensity_without_persisting_it() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        try
        {
            var settings = new AppSettings { Theme = new ThemeSettings { AccentIntensity = 100 } };
            w.ReplaceSettingsForTests(settings);
            var fullReach = ((SolidColorBrush)Application.Current.Resources["AccentChromeGlyph"]).Color;
            var textPrimary = ThemeColors.ParseColor(ThemeCatalog.PresetFor("sharp-dark").Palette.TextPrimary);

            // Drag the dial to 0. An intensity-only move must repaint even though the accent never
            // changed — if the two preview channels did not travel as a pair, the slider would look dead.
            w.QueueAccentIntensityPreviewForTests(0);
            w.FlushAccentPreviewForTests();

            Assert.Equal(textPrimary, ((SolidColorBrush)Application.Current.Resources["AccentChromeGlyph"]).Color);
            Assert.Equal(100, settings.Theme.AccentIntensity);   // previewed, NOT persisted

            // Dismiss without applying: rendering follows persisted state again.
            w.CancelQueuedAccentPreviewForTests();
            w.RevertPreviewedAccentForTests();

            Assert.Equal(100, w.EffectiveAccentIntensityForTests);
            Assert.Equal(fullReach, ((SolidColorBrush)Application.Current.Resources["AccentChromeGlyph"]).Color);
        }
        finally
        {
            w.CancelQueuedAccentPreviewForTests();
            w.ReplaceSettingsForTests(new AppSettings());
        }
    });

    [Fact]
    public void MainWindow_reverts_a_previewed_accent_on_dismiss() => StaTestThread.Invoke(() =>
    {
        // Spec D5 / plan-review P1: dismissing Settings without applying (ShowDialog() != true) and
        // cancelling the profile edit must revert every previewed accent surface to the resolved accent.
        var w = new MainWindow();
        w.ReplaceSettingsForTests(new AppSettings());   // global accent = default, no profile override
        var resolved = ThemeColors.DeriveAccentSet(
            w.ResolvedAccentColorForTests, ThemeCatalog.PresetFor("sharp-dark")).Primary;

        w.LivePreviewAccent("#9E84F0");   // preview a different accent
        Assert.NotEqual(resolved, ((SolidColorBrush)Application.Current.Resources["AccentPrimary"]).Color);

        w.RevertPreviewedAccentForTests();   // the dismiss-without-apply revert path
        Assert.Equal(resolved, ((SolidColorBrush)Application.Current.Resources["AccentPrimary"]).Color);
    });

    [Fact]
    public void MainWindow_live_preview_accepts_mid_tone_colors_and_ignores_invalid_hex() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        w.ReplaceSettingsForTests(new AppSettings());
        try
        {
            w.LivePreviewAccent("#787878");
            var previewed = ((SolidColorBrush)Application.Current.Resources["AccentPrimary"]).Color;
            Assert.Equal(ThemeColors.ParseColor("#787878"), previewed);

            var ex = Record.Exception(() => w.LivePreviewAccent("not-a-color"));
            Assert.Null(ex);
            Assert.Equal(previewed, ((SolidColorBrush)Application.Current.Resources["AccentPrimary"]).Color);
        }
        finally
        {
            w.RevertPreviewedAccentForTests();   // restore the App accent (avoid cross-test pollution)
        }
    });

    [Fact]
    public void PlayerWindow_uses_compact_minimum_and_clamps_launch_size_up() => StaTestThread.Invoke(() =>
    {
        // Launch below the compact floor: MinWidth/MinHeight must be the compact minimum and the
        // resolved launch size must clamp up to it (spec 10.2: no 320x180 for embed mode).
        var w = new PlayerWindow(
            environment: null!,
            url: "https://www.youtube.com/embed/dQw4w9WgXcQ?autoplay=1",
            topmost: false,
            placement: null,
            defaultWidth: 320,
            defaultHeight: 180,
            fadeEnabled: true,
            mode: PlaybackMode.Compact);

        Assert.Equal(PlaybackModePolicy.CompactMinWidth, w.MinWidth);
        Assert.Equal(PlaybackModePolicy.CompactMinHeight, w.MinHeight);
        Assert.True(w.Width >= PlaybackModePolicy.CompactMinWidth, $"Width {w.Width} below compact floor.");
        Assert.True(w.Height >= PlaybackModePolicy.CompactMinHeight, $"Height {w.Height} below compact floor.");
    });

    // --- Compact error bar + normal-page fallback (Phase 3, Stage 4: spec 10.3 / Q-6) ---

    private static PlayerWindow NewCompactPlayer() =>
        new(environment: null!, url: "https://piplay.local/player.html?v=dQw4w9WgXcQ",
            topmost: false, placement: null, defaultWidth: 960, defaultHeight: 540,
            fadeEnabled: true, mode: PlaybackMode.Compact,
            fallbackTarget: new YouTubeTarget { VideoId = "dQw4w9WgXcQ" });

    [Fact]
    public void Compact_error_bar_is_collapsed_on_construction() => StaTestThread.Invoke(() =>
    {
        Assert.False(NewCompactPlayer().IsErrorBarVisibleForTests);
    });

    [Fact]
    public void Shell_error_shows_the_bar_with_the_policy_message() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        w.HandleShellErrorForTests(new InboundShellMessage(ShellMessageKind.Error, ErrorCode: "101"));

        Assert.True(w.IsErrorBarVisibleForTests);
        Assert.Equal(PlayerShellErrorPolicy.Describe("101"), w.ErrorTextForTests);
    });

    [Fact]
    public void Shell_load_failure_shows_the_bar_with_the_load_message() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        w.HandleShellLoadFailureForTests();

        Assert.True(w.IsErrorBarVisibleForTests);
        Assert.Equal(PlayerShellErrorPolicy.ShellLoadFailedMessage, w.ErrorTextForTests);
    });

    [Fact]
    public void Playing_state_auto_dismisses_the_error_bar_but_others_do_not() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        w.HandleShellErrorForTests(new InboundShellMessage(ShellMessageKind.Error, ErrorCode: "100"));

        // Buffering does not prove recovery; the bar stays.
        w.HandleShellStateForTests(new InboundShellMessage(ShellMessageKind.State, CurrentTime: 0, PlayerState: 3));
        Assert.True(w.IsErrorBarVisibleForTests);

        // A playing state does (e.g. a playlist auto-advanced past the dead entry).
        w.HandleShellStateForTests(new InboundShellMessage(
            ShellMessageKind.State, CurrentTime: 7, PlayerState: PlayerShellErrorPolicy.StatePlaying));
        Assert.False(w.IsErrorBarVisibleForTests);
    });

    [Fact]
    public void Shell_errors_are_ignored_in_normal_mode() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();   // normal mode: no shell, so an error can't surface the compact bar
        w.HandleShellErrorForTests(new InboundShellMessage(ShellMessageKind.Error, ErrorCode: "101"));
        Assert.False(w.IsErrorBarVisibleForTests);
    });

    [Fact]
    public void Fallback_without_a_live_webview_is_a_guarded_no_op() => StaTestThread.Invoke(() =>
    {
        // The window is never shown, so CoreWebView2 was never created: the fallback must refuse
        // safely (no navigation target) and leave the error state and compact floor untouched.
        var w = NewCompactPlayer();
        w.HandleShellErrorForTests(new InboundShellMessage(ShellMessageKind.Error, ErrorCode: "150"));

        var ex = Record.Exception(w.RequestFallbackForTests);
        Assert.Null(ex);
        Assert.True(w.IsErrorBarVisibleForTests);
        Assert.Equal(PlaybackModePolicy.CompactMinWidth, w.MinWidth);
    });

    [Fact]
    public void SettingsWindow_compact_mode_is_a_silent_pass_through() => StaTestThread.Invoke(() =>
    {
        // The toggle was removed (2026-06); CompactMode is now a silent pass-through so the stored
        // value still round-trips via the settings-save caller without any UI surface.
        var on = new SettingsWindow(isBrowserReady: true, compactMode: true);
        Assert.True(on.CompactMode);
        Assert.Null(on.FindName("CompactModeToggle"));
        Assert.False(on.AppearanceChanged, "Storing compact mode in ctor must not mark settings dirty.");

        var off = new SettingsWindow(isBrowserReady: true, compactMode: false);
        Assert.False(off.CompactMode);
        Assert.Null(off.FindName("CompactModeToggle"));
    });

    [Fact]
    public void SettingsWindow_reflects_and_toggles_focused_presentation() => StaTestThread.Invoke(() =>
    {
        var on = new SettingsWindow(isBrowserReady: true, focusedPresentation: true);
        Assert.True(on.FocusedPresentation);
        Assert.True(((ToggleButton)on.FindName("FocusedOverlayToggle")!).IsChecked);
        Assert.False(on.AppearanceChanged, "Seeding Focused presentation must not mark settings dirty.");

        var standard = new SettingsWindow(isBrowserReady: true);
        Assert.False(standard.FocusedPresentation);
        var toggle = (ToggleButton)standard.FindName("FocusedOverlayToggle")!;
        Assert.False(toggle.IsChecked);

        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.True(standard.FocusedPresentation);
        Assert.True(standard.AppearanceChanged);
    });

    // --- Settings is bounded + scrollable (overhaul Task 5) ---

    [Fact]
    public void SettingsWindow_height_is_bounded_by_the_work_area() => StaTestThread.Invoke(() =>
    {
        var w = new SettingsWindow(isBrowserReady: true);

        // Pin the exact clamp derivation: work area less the margin, with the usability floor —
        // the floor WINS on a sub-468px work area, so asserting "<= work area" outright would
        // self-contradict the floor there (review finding 2026-06-11).
        Assert.True(double.IsFinite(w.MaxHeight), "Settings MaxHeight must be bounded.");
        Assert.Equal(Math.Max(420, SystemParameters.WorkArea.Height - 48), w.MaxHeight);
        // Frame model reconciled with the b35c0dd landing: fixed launch Height under the clamp.
        Assert.True(w.Height <= w.MaxHeight, $"Launch Height {w.Height} exceeds the clamp {w.MaxHeight}.");
        Assert.True(w.MinHeight > 0, "The dialog must declare a usable MinHeight.");
        Assert.IsType<ScrollViewer>(w.FindName("SettingsScroll"));
    });

    [Fact]
    public void SettingsWindow_compact_hint_text_is_not_present() => StaTestThread.Invoke(() =>
    {
        // The Playback section and its hint text were removed (2026-06) along with CompactModeToggle.
        Assert.Null(new SettingsWindow(isBrowserReady: true).FindName("CompactModeHintText"));
    });

    // --- Whole-window opacity (spec 7.3, Phase 4) ---

    [Fact]
    public void SettingsWindow_reflects_and_updates_window_opacity() => StaTestThread.Invoke(() =>
    {
        var w = new SettingsWindow(isBrowserReady: true, activeOpacityOverride: 0.8, idleOpacityOverride: 0.6);

        Assert.Equal(0.8, w.ConstantWindowOpacity);
        Assert.Equal(0.6, w.IdleWindowOpacity);
        Assert.Equal(80, ((Slider)w.FindName("ActiveOpacitySlider")!).Value);
        Assert.Equal(60, ((Slider)w.FindName("IdleOpacitySlider")!).Value);
        Assert.False(w.AppearanceChanged);   // seeding the sliders must not count as a user change

        (double Constant, double Idle)? preview = null;
        w.OpacityPreviewChanged += (c, i) => preview = (c, i);
        ((Slider)w.FindName("IdleOpacitySlider")!).Value = 45;

        Assert.True(w.AppearanceChanged);
        Assert.Equal(0.45, w.IdleWindowOpacity);
        Assert.Equal((0.8, 0.45), preview);
    });

    [Fact]
    public void SettingsWindow_preserves_hand_edited_sub_floor_opacity_until_the_slider_moves() => StaTestThread.Invoke(() =>
    {
        // Spec 7.3 explicit unlock: a hand-edited 0.25 displays clamped at the 45% slider floor
        // but must survive an unrelated settings change untouched.
        var w = new SettingsWindow(isBrowserReady: true, activeOpacityOverride: 0.25, idleOpacityOverride: 1.0);

        Assert.Equal(45, ((Slider)w.FindName("ActiveOpacitySlider")!).Value);
        Assert.Equal(0.25, w.ConstantWindowOpacity);

        ((Slider)w.FindName("IdleOpacitySlider")!).Value = 70;   // user touches only the idle slider
        Assert.Equal(0.25, w.ConstantWindowOpacity);             // the unlock survives
        Assert.Equal(0.7, w.IdleWindowOpacity);

        ((Slider)w.FindName("ActiveOpacitySlider")!).Value = 50; // moving THE slider replaces it
        Assert.Equal(0.5, w.ConstantWindowOpacity);
    });

    [Fact]
    public void PlayerWindow_records_normalized_window_opacity_levels() => StaTestThread.Invoke(() =>
    {
        var w = new PlayerWindow(environment: null!, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false, placement: null, defaultWidth: 960, defaultHeight: 540, fadeEnabled: true,
            constantWindowOpacity: 0.8, idleWindowOpacity: 5.0);

        Assert.Equal((0.8, 1.0), w.WindowOpacityLevelsForTests);   // junk idle reset by the policy

        w.ApplyWindowOpacity(0.6, 0.45);
        Assert.Equal((0.6, 0.45), w.WindowOpacityLevelsForTests);
    });

    [Fact]
    public void PlayerWindow_opacity_idle_state_is_safe_without_an_hwnd() => StaTestThread.Invoke(() =>
    {
        // Never shown: entering/leaving the opacity idle state must not start the hover poll or
        // touch native state (the SourceInitialized hook owns the first real application).
        var w = NewPlayer();
        var ex = Record.Exception(() =>
        {
            w.EnterWindowOpacityIdleForTests();
            Assert.True(w.IsWindowOpacityIdleForTests);
            Assert.False(w.IsOpacityHoverPollRunningForTests);
        });
        Assert.Null(ex);
    });

    [Fact]
    public void Window_opacity_applier_engages_a_real_hwnd_and_disengages_cleanly() => StaTestThread.Invoke(() =>
    {
        // Real HWND (the BorderlessWindowHelper test recipe): the guard subclass must hold the
        // layered bit against WPF's HwndTarget strip, and 1.0 must restore the pristine exstyle.
        var w = new Window
        {
            Width = 240, Height = 160, Left = 100, Top = 100,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
            AllowsTransparency = false, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
        };
        var hwnd = new WindowInteropHelper(w).EnsureHandle();

        // Acceptance criterion 1: feature-off calls on a pristine window are a strict no-op —
        // no tracking state, no subclass-driven exstyle change (Null target proves never tracked).
        var pristine = GetWindowLongPtrW(hwnd, -20).ToInt64();
        WindowOpacityApplier.SetCornerMode(hwnd, DwmCornerMode.Default);
        WindowOpacityApplier.Apply(hwnd, 1.0, animate: false);
        Assert.Null(WindowOpacityApplier.TargetAlphaForTests(hwnd));
        Assert.Equal(DwmCornerMode.Default, WindowOpacityApplier.CornerModeForTests(hwnd));
        Assert.Equal(pristine, GetWindowLongPtrW(hwnd, -20).ToInt64());

        // Engage BEFORE Show: production order (the popout's SourceInitialized runs inside Show),
        // so the bit + alpha must survive WPF's show-time style application.
        WindowOpacityApplier.Apply(hwnd, 0.6, animate: false);
        w.Show();
        Assert.True(WindowOpacityApplier.IsEngagedForTests(hwnd));
        Assert.Equal((byte)153, WindowOpacityApplier.TargetAlphaForTests(hwnd));
        Assert.Equal((byte)153, WindowOpacityApplier.CurrentAlphaForTests(hwnd));
        var ex = GetWindowLongPtrW(hwnd, -20).ToInt64();
        Assert.True((ex & 0x00080000) != 0, $"WS_EX_LAYERED missing from live exstyle 0x{ex:X} after Show.");
        Assert.True((ex & 0x00000020) == 0, $"WS_EX_TRANSPARENT set on live exstyle 0x{ex:X} (ADR-0006).");
        Assert.False(WindowOpacityApplier.LastExStyleWriteCarriedTransparentBitForTests(hwnd));

        // Spike finding 2: a wholesale exstyle rewrite that drops the bit (what WPF does during
        // move/size/topmost) must be defeated by the WM_STYLECHANGING forcing, not healed later.
        SetWindowLongPtrW(hwnd, -20, new IntPtr(ex & ~0x00080000));
        var afterHostileWrite = GetWindowLongPtrW(hwnd, -20).ToInt64();
        Assert.True((afterHostileWrite & 0x00080000) != 0,
            $"Guard failed to force WS_EX_LAYERED through a hostile rewrite: 0x{afterHostileWrite:X}.");
        w.Topmost = true;   // production-shaped stressor on the same path
        var afterTopmost = GetWindowLongPtrW(hwnd, -20).ToInt64();
        Assert.True((afterTopmost & 0x00080000) != 0, $"Topmost toggle dropped the bit: 0x{afterTopmost:X}.");

        WindowOpacityApplier.SetCornerMode(hwnd, DwmCornerMode.Round);
        Assert.Equal(DwmCornerMode.Round, WindowOpacityApplier.CornerModeForTests(hwnd));
        // Mode is theme/user data: every non-default mode (incl. an explicit Square override on a
        // previously-rounded window) lands and is tracked.
        WindowOpacityApplier.SetCornerMode(hwnd, DwmCornerMode.Square);
        Assert.Equal(DwmCornerMode.Square, WindowOpacityApplier.CornerModeForTests(hwnd));
        WindowOpacityApplier.SetCornerMode(hwnd, DwmCornerMode.SmallRound);
        Assert.Equal(DwmCornerMode.SmallRound, WindowOpacityApplier.CornerModeForTests(hwnd));
        // The reset transition is a real user path (corner style back to Theme on sharp-dark /
        // theme switched off soft-glass): Default on a MODIFIED window must land DWMWCP_DEFAULT,
        // not early-return — only the never-touched pristine case skips the write.
        WindowOpacityApplier.SetCornerMode(hwnd, DwmCornerMode.Default);
        Assert.Equal(DwmCornerMode.Default, WindowOpacityApplier.CornerModeForTests(hwnd));

        WindowOpacityApplier.Apply(hwnd, 1.0, animate: false);
        Assert.False(WindowOpacityApplier.IsEngagedForTests(hwnd));
        var restored = GetWindowLongPtrW(hwnd, -20).ToInt64();
        Assert.True((restored & 0x00080000) == 0, $"WS_EX_LAYERED still set after disengage: 0x{restored:X}.");

        // Re-engage after a disengage (the constant=1.0 + idle<1.0 cycle hits this every round trip).
        WindowOpacityApplier.Apply(hwnd, 0.45, animate: false);
        Assert.True(WindowOpacityApplier.IsEngagedForTests(hwnd));
        Assert.Equal((byte)115, WindowOpacityApplier.CurrentAlphaForTests(hwnd));
        var reengaged = GetWindowLongPtrW(hwnd, -20).ToInt64();
        Assert.True((reengaged & 0x00080000) != 0, $"Re-engage failed to land the bit: 0x{reengaged:X}.");
    });

    [Fact]
    public void Border_color_suppression_does_not_engage_the_opacity_guard() => StaTestThread.Invoke(() =>
    {
        // A window that only ever gets a DWM border-color write must stay "never tracked" for the
        // opacity guard: DWMWA_BORDER_COLOR is a DWM composition attribute, not a WS_EX_LAYERED/alpha
        // change, so SetBorderColor must not install the subclass or create a States entry — otherwise
        // the opacity no-op invariant (pristine exstyle, TargetAlpha null) breaks.
        WindowOpacityApplier.ResetBorderSuppressionForTests();   // deterministic "never recorded" baseline
        var w = new Window
        {
            Width = 240, Height = 160, Left = 100, Top = 100,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
            AllowsTransparency = false, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
        };
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        var pristine = GetWindowLongPtrW(hwnd, -20).ToInt64();

        // Never called → no recorded intent (mirrors the opacity TargetAlpha==null "never tracked" gate).
        Assert.Null(WindowOpacityApplier.BorderColorSuppressedForTests(hwnd));

        // Explicit highContrast keeps the assertion deterministic regardless of the host's setting.
        WindowOpacityApplier.SetBorderColor(hwnd, suppress: true, highContrast: false);
        Assert.True(WindowOpacityApplier.BorderColorSuppressedForTests(hwnd));

        // The border-color write perturbs no exstyle bit and engages no opacity tracking.
        Assert.Equal(pristine, GetWindowLongPtrW(hwnd, -20).ToInt64());
        Assert.Null(WindowOpacityApplier.TargetAlphaForTests(hwnd));
        Assert.False(WindowOpacityApplier.IsEngagedForTests(hwnd));

        // The "borders on" / restore path records not-suppressed.
        WindowOpacityApplier.SetBorderColor(hwnd, suppress: false, highContrast: false);
        Assert.False(WindowOpacityApplier.BorderColorSuppressedForTests(hwnd));

        w.Close();
    });

    [Fact]
    public void Border_color_keeps_the_system_border_under_high_contrast() => StaTestThread.Invoke(() =>
    {
        // Accessibility: borderless is the default, but High Contrast must keep the system frame
        // border (a boundary/focus cue). The production method consults the real HC state; the
        // overload injects it so BOTH branches are pinned without depending on the host's setting.
        var w = new Window
        {
            Width = 240, Height = 160, Left = 100, Top = 100,
            WindowStyle = WindowStyle.None, AllowsTransparency = false,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
        };
        var hwnd = new WindowInteropHelper(w).EnsureHandle();

        WindowOpacityApplier.SetBorderColor(hwnd, suppress: true, highContrast: true);
        Assert.False(WindowOpacityApplier.BorderColorSuppressedForTests(hwnd));   // HC → keep the border

        WindowOpacityApplier.SetBorderColor(hwnd, suppress: true, highContrast: false);
        Assert.True(WindowOpacityApplier.BorderColorSuppressedForTests(hwnd));    // normal → suppress

        w.Close();
    });

    [Fact]
    public void Border_color_records_nothing_in_the_production_default() => StaTestThread.Invoke(() =>
    {
        // The border record is a test seam, and border-only windows carry no subclass, so nothing
        // reclaims its entries on window destruction. Production therefore records NOTHING: without
        // this gate the dict would grow one entry per top-level HWND ever shown. This test runs with
        // the class-wide arming turned back off, which is the shipping configuration.
        WindowOpacityApplier.DisableBorderSuppressionTrackingForTests();
        try
        {
            WindowOpacityApplier.ResetBorderSuppressionForTests();
            var w = new Window
            {
                Width = 240, Height = 160, Left = 100, Top = 100,
                WindowStyle = WindowStyle.None, AllowsTransparency = false,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            };
            var hwnd = new WindowInteropHelper(w).EnsureHandle();

            WindowOpacityApplier.SetBorderColor(hwnd, suppress: true, highContrast: false);
            WindowOpacityApplier.SetBorderColor(hwnd, suppress: false, highContrast: false);

            Assert.Equal(0, WindowOpacityApplier.BorderSuppressionEntryCountForTests());
            Assert.Null(WindowOpacityApplier.BorderColorSuppressedForTests(hwnd));

            w.Close();
        }
        finally
        {
            WindowOpacityApplier.EnableBorderSuppressionTrackingForTests();   // restore the test-lane arming
        }
    });

    [Fact]
    public void PlayerWindow_applies_active_and_idle_levels_through_the_applier() => StaTestThread.Invoke(() =>
    {
        // EnsureHandle fires SourceInitialized without Show: Loaded never runs, so WebView2 and
        // the network stay untouched, but the initial opacity application is real.
        var w = new PlayerWindow(environment: null!, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false, placement: null, defaultWidth: 960, defaultHeight: 540, fadeEnabled: true,
            constantWindowOpacity: 0.8, idleWindowOpacity: 0.6);
        var hwnd = new WindowInteropHelper(w).EnsureHandle();

        // SourceInitialized applied the ACTIVE level (the "appear at the configured level" hook).
        Assert.True(WindowOpacityApplier.IsEngagedForTests(hwnd));
        Assert.Equal(WindowOpacityPolicy.ToAlphaByte(0.8), WindowOpacityApplier.TargetAlphaForTests(hwnd));

        // Idle onset applies the idle level and arms the hover-restore poll (idle < active).
        w.EnterWindowOpacityIdleForTests();
        Assert.Equal(WindowOpacityPolicy.ToAlphaByte(0.6), WindowOpacityApplier.TargetAlphaForTests(hwnd));
        Assert.True(w.IsOpacityHoverPollRunningForTests);

        // Settings change while idle keeps applying the IDLE level (the live-preview path).
        w.ApplyWindowOpacity(0.7, 0.5);
        Assert.Equal(WindowOpacityPolicy.ToAlphaByte(0.5), WindowOpacityApplier.TargetAlphaForTests(hwnd));

        // Activity restores the active level; the probe keeps running while an idle dip is
        // configured (it must PREVENT the next idle onset during movement over the video).
        w.OnUserActivityForTests();
        Assert.False(w.IsWindowOpacityIdleForTests);
        Assert.Equal(WindowOpacityPolicy.ToAlphaByte(0.7), WindowOpacityApplier.TargetAlphaForTests(hwnd));
        Assert.True(w.IsOpacityHoverPollRunningForTests);

        // Turning the feature off stops the probe (defaults never run it).
        w.ApplyWindowOpacity(1.0, 1.0);
        Assert.False(w.IsOpacityHoverPollRunningForTests);

        w.Close();
    });

    [Fact]
    public void PlayerWindow_applies_the_theme_corner_mode_to_its_hwnd() => StaTestThread.Invoke(() =>
    {
        // End-to-end corner wiring (the opacity test's recipe): EnsureHandle fires
        // SourceInitialized without Show, so the initial DWM corner application is real.
        var w = new PlayerWindow(environment: null!, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false, placement: null, defaultWidth: 960, defaultHeight: 540, fadeEnabled: true,
            dwmCornerMode: DwmCornerMode.Round);
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        Assert.Equal(DwmCornerMode.Round, WindowOpacityApplier.CornerModeForTests(hwnd));

        // The live re-apply seam (MainWindow's ApplyOpenPlayerAppearance path on settings change).
        w.ApplyCornerMode(DwmCornerMode.Square);
        Assert.Equal(DwmCornerMode.Square, WindowOpacityApplier.CornerModeForTests(hwnd));

        w.Close();
    });

    [Fact]
    public void PlayerWindow_round_mode_applies_the_large_region_and_clears_it_for_square_or_maximized() =>
        StaTestThread.Invoke(() =>
        {
            var w = new PlayerWindow(
                environment: null!,
                url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                topmost: false,
                placement: null,
                defaultWidth: 960,
                defaultHeight: 540,
                fadeEnabled: true,
                dwmCornerMode: DwmCornerMode.Round,
                popoutCornerRadiusDip: 22)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 240,
                Top = 180,
            };

            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            w.ApplyCornerAppearance(DwmCornerMode.Round, 22); // re-evaluate after the manual position lands
            Assert.True(RoundedWindowRegionApplier.HasCustomRegionForTests(hwnd));
            Assert.False(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, 0, 0));
            Assert.True(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, 100, 100));

            w.ApplyCornerAppearance(DwmCornerMode.Square, 0);
            Assert.False(RoundedWindowRegionApplier.HasCustomRegionForTests(hwnd));
            Assert.True(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, 0, 0));

            w.WindowState = WindowState.Maximized;
            w.ApplyCornerAppearance(DwmCornerMode.Round, 22);
            Assert.False(RoundedWindowRegionApplier.HasCustomRegionForTests(hwnd));

            w.WindowState = WindowState.Normal;
            w.ApplyCornerAppearance(DwmCornerMode.Round, 22);
            Assert.True(RoundedWindowRegionApplier.HasCustomRegionForTests(hwnd));
            w.Close();
        });

    [Theory]
    [InlineData(320, 180)]
    [InlineData(480, 270)]
    [InlineData(640, 360)]
    public void Rounded_region_keeps_the_final_pixel_of_every_straight_edge_and_cuts_all_four_corners(
        int widthPx,
        int heightPx) =>
        StaTestThread.Invoke(() =>
        {
            // The review plan's native probe sizes: a 22-DIP Popout radius at 96/144/192 DPI lands
            // on exactly these physical proportions, so the three cases represent the supported
            // scale range. A plain borderless window gives exact physical control of the region.
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            try
            {
                var hwnd = new WindowInteropHelper(w).EnsureHandle();
                Assert.True(SetWindowPos(hwnd, IntPtr.Zero, 80, 80, widthPx, heightPx, SwpNoZOrder | SwpNoActivate));
                Assert.True(GetWindowRect(hwnd, out var rect));
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                Assert.Equal(widthPx, width);
                Assert.Equal(heightPx, height);

                Assert.True(RoundedWindowRegionApplier.Apply(hwnd, 22));
                Assert.True(RoundedWindowRegionApplier.HasCustomRegionForTests(hwnd));

                // All four corners stay cut.
                Assert.False(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, 0, 0));
                Assert.False(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, width - 1, 0));
                Assert.False(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, 0, height - 1));
                Assert.False(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, width - 1, height - 1));
                // Every straight edge keeps its final pixel (the right/bottom pair is the endpoint
                // regression: GDI dropped them before the exclusive endpoints ran one past the bounds).
                Assert.True(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, 0, height / 2));
                Assert.True(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, width / 2, 0));
                Assert.True(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, width - 1, height / 2));
                Assert.True(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, width / 2, height - 1));
                // The region still ends with the window: one pixel past the last row/column is outside.
                Assert.False(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, width, height / 2));
                Assert.False(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, width / 2, height));
                Assert.True(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, width / 2, height / 2));
            }
            finally
            {
                w.Close();
            }
        });

    [Fact]
    public void Rounded_region_clears_for_a_snap_like_placement_and_reapplies_when_floating_again() =>
        StaTestThread.Invoke(() =>
        {
            var w = new PlayerWindow(
                environment: null!,
                url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                topmost: false,
                placement: null,
                defaultWidth: 960,
                defaultHeight: 540,
                fadeEnabled: true,
                dwmCornerMode: DwmCornerMode.Round,
                popoutCornerRadiusDip: 22)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            try
            {
                var hwnd = new WindowInteropHelper(w).EnsureHandle();

                // Floating start: the rounded region is on.
                Assert.True(SetWindowPos(hwnd, IntPtr.Zero, 80, 80, 480, 270, SwpNoZOrder | SwpNoActivate));
                w.ApplyCornerAppearance(DwmCornerMode.Round, 22);
                Assert.True(RoundedWindowRegionApplier.HasCustomRegionForTests(hwnd));
                Assert.False(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, 0, 0));

                // Place as the OS would snap a left half: work-area top-left, half width, full height.
                var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
                Assert.True(GetMonitorInfo(monitor, ref info));
                var halfWidth = (info.Work.Right - info.Work.Left) / 2;
                var workHeight = info.Work.Bottom - info.Work.Top;
                Assert.True(SetWindowPos(
                    hwnd, IntPtr.Zero, info.Work.Left, info.Work.Top, halfWidth, workHeight,
                    SwpNoZOrder | SwpNoActivate));
                Assert.True(RoundedWindowRegionApplier.IsSnapLike(hwnd));

                w.ApplyCornerAppearance(DwmCornerMode.Round, 22);
                Assert.False(RoundedWindowRegionApplier.HasCustomRegionForTests(hwnd));
                Assert.True(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, 0, 0));

                // Floating restore reapplies the region (ADR: maximize/snap clears, floating reapplies).
                Assert.True(SetWindowPos(hwnd, IntPtr.Zero, 80, 80, 480, 270, SwpNoZOrder | SwpNoActivate));
                w.ApplyCornerAppearance(DwmCornerMode.Round, 22);
                Assert.True(RoundedWindowRegionApplier.HasCustomRegionForTests(hwnd));
                Assert.False(RoundedWindowRegionApplier.IsPointVisibleForTests(hwnd, 0, 0));
            }
            finally
            {
                w.Close();   // a failed assertion must not leave a native window behind for later tests
            }
        });

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [Fact]
    public void MainWindow_applies_the_theme_corner_mode_at_source_initialized() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        w.ReplaceSettingsForTests(new AppSettings { Theme = new ThemeSettings { CornerStyle = "round" } });
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        Assert.Equal(DwmCornerMode.Round, WindowOpacityApplier.CornerModeForTests(hwnd));
        w.Close();
    });

    [Fact]
    public void SettingsWindow_wears_the_pending_corner_style_on_its_own_hwnd() => StaTestThread.Invoke(() =>
    {
        // The dialog itself is the instant feedback surface for the corner-style row.
        var w = new SettingsWindow(isBrowserReady: true, cornerStyle: "round");
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        Assert.Equal(DwmCornerMode.Round, WindowOpacityApplier.CornerModeForTests(hwnd));

        ((ToggleButton)w.FindName("CornerStyleSquareChip")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(DwmCornerMode.Square, WindowOpacityApplier.CornerModeForTests(hwnd));

        w.Close();
    });

    [Fact]
    public void Prompt_dialogs_wear_the_current_theme_corner_mode() => StaTestThread.Invoke(() =>
    {
        // Prompts are built outside the settings flow, so they read the applier's last-applied
        // mode — without this they would be the only differently-shaped dialog under a
        // round/square theme (adversarial review finding).
        var res = new ResourceDictionary();
        ThemeResourceApplier.Apply(res, new ThemeSettings { ThemeId = "soft-glass" }, new PlayerSettings());
        try
        {
            Assert.Equal(DwmCornerMode.Round, ThemeResourceApplier.CurrentDwmCorners);
            var shell = Prompt.BuildShell(owner: null, "Test", out _);
            var hwnd = new WindowInteropHelper(shell).EnsureHandle();
            Assert.Equal(DwmCornerMode.Round, WindowOpacityApplier.CornerModeForTests(hwnd));
            shell.Close();
        }
        finally
        {
            // Restore the static for the rest of the suite (sharp-dark default = Default).
            ThemeResourceApplier.Apply(new ResourceDictionary(), new ThemeSettings(), new PlayerSettings());
        }
    });

    // --- Borderless follow-up: every borderless window suppresses the Windows 11 DWM frame border ---
    // (P1's missed gap: the default sharp-dark theme is DwmCornerMode.Default, so SetCornerMode
    // early-returns and never touched DWM — leaving the grey system hairline. Border suppression is
    // unconditional, so these assert it lands even with no corner override.) Each asserts NotNull —
    // i.e. the window WIRED a border-color decision — which proves the wiring independently of the
    // host's High Contrast state (the resolved value itself is pinned by the HC test above).

    [Fact]
    public void MainWindow_suppresses_its_dwm_frame_border_at_source_initialized() => StaTestThread.Invoke(() =>
    {
        WindowOpacityApplier.ResetBorderSuppressionForTests();   // no stale recycled-HWND entry can weaken the check
        var w = new MainWindow();
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        Assert.NotNull(WindowOpacityApplier.BorderColorSuppressedForTests(hwnd));
        w.Close();
    });

    [Fact]
    public void PlayerWindow_suppresses_its_dwm_frame_border_at_source_initialized() => StaTestThread.Invoke(() =>
    {
        WindowOpacityApplier.ResetBorderSuppressionForTests();   // no stale recycled-HWND entry can weaken the check
        var w = new PlayerWindow(environment: null!, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false, placement: null, defaultWidth: 960, defaultHeight: 540, fadeEnabled: true);
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        Assert.NotNull(WindowOpacityApplier.BorderColorSuppressedForTests(hwnd));
        w.Close();
    });

    [Fact]
    public void SettingsWindow_suppresses_its_dwm_frame_border_on_its_own_hwnd() => StaTestThread.Invoke(() =>
    {
        WindowOpacityApplier.ResetBorderSuppressionForTests();   // no stale recycled-HWND entry can weaken the check
        var w = new SettingsWindow(isBrowserReady: true, cornerStyle: "round");
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        Assert.NotNull(WindowOpacityApplier.BorderColorSuppressedForTests(hwnd));
        w.Close();
    });

    [Fact]
    public void Prompt_dialogs_suppress_the_dwm_frame_border() => StaTestThread.Invoke(() =>
    {
        WindowOpacityApplier.ResetBorderSuppressionForTests();   // no stale recycled-HWND entry can weaken the check
        var shell = Prompt.BuildShell(owner: null, "Test", out _);
        var hwnd = new WindowInteropHelper(shell).EnsureHandle();
        Assert.NotNull(WindowOpacityApplier.BorderColorSuppressedForTests(hwnd));
        shell.Close();
    });

    [Fact]
    public void Prompt_shell_has_no_visible_inner_border() => StaTestThread.Invoke(() =>
    {
        // P1 borderless: with the DWM frame suppressed, the prompt's content Border must not redraw a
        // 1px inner frame; the prompt's inner border remains transparent.
        var shell = Prompt.BuildShell(owner: null, "Test", out _);
        var border = Assert.IsType<Border>(shell.Content);
        Assert.Equal(new Thickness(0), border.BorderThickness);
        shell.Close();
    });

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr newValue);

    // --- Shell request channel + strip auto-hide (spec 7.2 / 10.3, Phase 4 Task 4) ---

    private static PlayerWindow NewCompactAutoHidePlayer() =>
        new(environment: null!, url: "https://piplay.local/player.html?v=dQw4w9WgXcQ",
            topmost: false, placement: null, defaultWidth: 960, defaultHeight: 540,
            fadeEnabled: true, mode: PlaybackMode.Compact,
            fallbackTarget: new YouTubeTarget { VideoId = "dQw4w9WgXcQ" }, stripAutoHide: true);

    private static InboundShellMessage Request(string action) =>
        new(ShellMessageKind.Request, Action: action);

    [Fact]
    public void Shell_close_request_closes_the_player() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        var closed = false;
        w.PlayerClosed += (_, _) => closed = true;

        w.HandleShellRequestForTests(Request(PlayerShellProtocol.ActionClose));
        // Close is deferred out of the WebMessageReceived callback (WebView2 reentrancy guard);
        // a Background-priority nested invoke drains the queued Normal-priority Close first.
        Assert.False(closed);
        w.Dispatcher.Invoke(static () => { }, DispatcherPriority.Background);

        Assert.True(closed);
    });

    [Fact]
    public void Shell_pin_request_toggles_topmost_and_the_strip_toggle() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        Assert.False(w.Topmost);

        w.HandleShellRequestForTests(Request(PlayerShellProtocol.ActionPinToggle));
        Assert.True(w.Topmost);
        Assert.True(((ToggleButton)w.FindName("PinToggle")!).IsChecked);   // native toggle stays in sync

        w.HandleShellRequestForTests(Request(PlayerShellProtocol.ActionPinToggle));
        Assert.False(w.Topmost);
        Assert.False(((ToggleButton)w.FindName("PinToggle")!).IsChecked);
    });

    [Fact]
    public void Shell_fullscreen_request_toggles_maximized() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        Assert.Equal(WindowState.Normal, w.WindowState);

        w.HandleShellRequestForTests(Request(PlayerShellProtocol.ActionFullscreenToggle));
        Assert.Equal(WindowState.Maximized, w.WindowState);

        w.HandleShellRequestForTests(Request(PlayerShellProtocol.ActionFullscreenToggle));
        Assert.Equal(WindowState.Normal, w.WindowState);
    });

    [Fact]
    public void Focused_settings_request_keeps_settings_reachable_from_the_popout() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        var requests = 0;
        w.SettingsRequested += (_, _) => requests++;

        w.HandleFocusedActionForTests(PlayerFirstSurfaceProtocol.ActionSettings);
        Assert.Equal(0, requests); // deferred out of WebMessageReceived
        w.Dispatcher.Invoke(static () => { }, DispatcherPriority.Background);

        Assert.Equal(1, requests);
    });

    // --- Expand / restore affordance (overhaul Task 4) ---

    private const string GlyphMaximize = "";
    private const string GlyphRestore = "";

    private static void ClickExpand(PlayerWindow w) =>
        ((Button)w.FindName("ExpandButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Fact]
    public void Expand_button_toggles_maximized_in_normal_mode_and_keeps_the_affordance_honest() =>
        StaTestThread.Invoke(() =>
        {
            // Normal mode deliberately: the native button must serve BOTH playback modes.
            var w = NewPlayer();
            Assert.Equal(GlyphMaximize, w.ExpandGlyphForTests);
            Assert.Equal("Expand popout", w.ExpandToolTipForTests);

            ClickExpand(w);
            Assert.Equal(WindowState.Maximized, w.WindowState);
            Assert.Equal(GlyphRestore, w.ExpandGlyphForTests);
            Assert.Equal("Restore popout", w.ExpandToolTipForTests);

            ClickExpand(w);
            Assert.Equal(WindowState.Normal, w.WindowState);
            Assert.Equal(GlyphMaximize, w.ExpandGlyphForTests);
            Assert.Equal("Expand popout", w.ExpandToolTipForTests);
        });

    [Fact]
    public void Shell_fullscreen_request_updates_the_expand_affordance() => StaTestThread.Invoke(() =>
    {
        // The shell request and the native button are ONE path; the glyph follows either caller.
        var w = NewCompactPlayer();
        w.HandleShellRequestForTests(Request(PlayerShellProtocol.ActionFullscreenToggle));
        Assert.Equal(GlyphRestore, w.ExpandGlyphForTests);
    });

    [Fact]
    public void Fullscreen_element_expands_compact_and_exit_restores() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();

        w.ApplyFullScreenElementStateForTests(contains: true);
        Assert.Equal(WindowState.Maximized, w.WindowState);
        Assert.Equal(GlyphRestore, w.ExpandGlyphForTests);

        w.ApplyFullScreenElementStateForTests(contains: false);
        Assert.Equal(WindowState.Normal, w.WindowState);
        Assert.Equal(GlyphMaximize, w.ExpandGlyphForTests);
    });

    [Fact]
    public void Fullscreen_element_is_ignored_in_normal_mode() => StaTestThread.Invoke(() =>
    {
        // The gate is the LIVE mode (Popout Standard / Fullview Faded must not gain a new
        // fullscreen invariant) — same gate the compact→normal fallback relies on.
        var w = NewPlayer();
        w.ApplyFullScreenElementStateForTests(contains: true);
        Assert.Equal(WindowState.Normal, w.WindowState);
    });

    [Fact]
    public void Fullscreen_element_exit_keeps_a_user_expanded_window() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        ClickExpand(w);   // the user's own posture, not the element's
        Assert.Equal(WindowState.Maximized, w.WindowState);

        w.ApplyFullScreenElementStateForTests(contains: true);
        w.ApplyFullScreenElementStateForTests(contains: false);
        Assert.Equal(WindowState.Maximized, w.WindowState);
    });

    [Fact]
    public void Expand_toggle_reveals_a_collapsed_strip_so_restore_stays_reachable() => StaTestThread.Invoke(() =>
    {
        // Adopted from the parallel b35c0dd landing: any expand path counts as activity, so an
        // auto-hidden strip un-collapses and the restore affordance is reachable in the new state
        // without waiting for the top-edge reveal (Task 4 reversibility).
        var w = NewCompactAutoHidePlayer();
        w.HideControlsForTests();
        w.CompleteHideFadeForTests();
        Assert.True(w.IsChromeStripCollapsedForTests);

        ClickExpand(w);
        Assert.Equal(WindowState.Maximized, w.WindowState);
        Assert.False(w.IsChromeStripCollapsedForTests);
        Assert.True(w.IsChromeStripHitTestVisibleForTests);
    });

    [Fact]
    public void Os_restore_clears_the_fullscreen_element_latch() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        w.ApplyFullScreenElementStateForTests(contains: true);
        Assert.True(w.IsMaximizedForFullScreenElementForTests);

        // The OS path our toggles never see (Win+Down, aero snap): the state changes, then
        // StateChanged runs the sync — the expansion the latch described no longer exists.
        w.WindowState = WindowState.Normal;
        w.HandleWindowStateChangedForTests();
        Assert.False(w.IsMaximizedForFullScreenElementForTests);

        // The element's later exit must not disturb the state the user chose via the OS.
        w.ApplyFullScreenElementStateForTests(contains: false);
        Assert.Equal(WindowState.Normal, w.WindowState);
    });

    [Fact]
    public void Escape_restores_an_expanded_popout_and_is_inert_otherwise() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        Assert.False(w.HandleEscapeForTests());

        ClickExpand(w);
        Assert.True(w.HandleEscapeForTests());
        Assert.Equal(WindowState.Normal, w.WindowState);
        Assert.Equal(GlyphMaximize, w.ExpandGlyphForTests);
    });

    [Fact]
    public void Popout_never_launches_expanded_even_from_a_maximized_capture() => StaTestThread.Invoke(() =>
    {
        // Pre-fix settings files can carry Maximized=true; the ctor normalizes it away.
        var w = new PlayerWindow(environment: null!, url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false,
            placement: new PlacementData { X = 20, Y = 20, Width = 900, Height = 560, Maximized = true },
            defaultWidth: 960, defaultHeight: 540, fadeEnabled: true);

        Assert.NotNull(w.LaunchPlacementForTests);
        Assert.False(w.LaunchPlacementForTests!.Maximized);
        Assert.Equal(900, w.LaunchPlacementForTests.Width);   // bounds survive the normalization
    });

    // --- In-place retarget + video-aware return state (overhaul Task 3) ---

    [Fact]
    public void Compact_player_carries_its_launch_video_in_the_return_state() => StaTestThread.Invoke(() =>
    {
        Assert.Equal("dQw4w9WgXcQ", NewCompactPlayer().ReturnVideoIdForTests);
    });

    [Fact]
    public void Shell_state_updates_the_return_video_and_timestamp() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        w.HandleShellStateForTests(new InboundShellMessage(
            ShellMessageKind.State, CurrentTime: 42, PlayerState: 1, VideoId: "autoAdvVid1"));

        Assert.Equal("autoAdvVid1", w.ReturnVideoIdForTests);   // playlist auto-advance tracked
        Assert.Equal(42, w.ReturnSecondsForTests);

        // A state message WITHOUT a videoId (pre-v3 shape) keeps the last-known id.
        w.HandleShellStateForTests(new InboundShellMessage(ShellMessageKind.State, CurrentTime: 50));
        Assert.Equal("autoAdvVid1", w.ReturnVideoIdForTests);
        Assert.Equal(50, w.ReturnSecondsForTests);
    });

    [Fact]
    public void Hostile_shell_video_ids_never_become_the_return_target() => StaTestThread.Invoke(() =>
    {
        // End-to-end through the REAL wire path: the shell string later becomes a SOURCE
        // navigation target on close, and PlayerShellProtocol.Parse (the trust boundary) must
        // reject the malformed id before the host ever sees it.
        var w = NewCompactPlayer();
        w.HandleShellStateForTests(PlayerShellProtocol.Parse(
            "{\"v\":3,\"type\":\"state\",\"currentTime\":5,\"playerState\":1,\"videoId\":\"abc&evil=1//\"}"));

        Assert.Equal("dQw4w9WgXcQ", w.ReturnVideoIdForTests);   // launch id kept
        Assert.Equal(5, w.ReturnSecondsForTests);               // the timestamp itself still lands
    });

    [Fact]
    public void Compact_retarget_rebuilds_the_shell_url_and_resets_launch_state() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        w.HandleShellStateForTests(new InboundShellMessage(ShellMessageKind.State, CurrentTime: 42, PlayerState: 1));

        Assert.True(w.TryRetargetForNewWindow("https://www.youtube.com/watch?v=recVideo001&t=30s"));

        Assert.Contains("piplay.local/player.html", w.CurrentUrlForTests);   // same mode: shell rebuild
        Assert.Contains("v=recVideo001", w.CurrentUrlForTests);
        Assert.Contains("start=30", w.CurrentUrlForTests);
        Assert.Equal("recVideo001", w.ReturnVideoIdForTests);
        Assert.Equal("recVideo001", w.CurrentFallbackVideoIdForTests);   // error-bar fallback follows
        Assert.Null(w.ReturnSecondsForTests);   // unknown until the NEW shell reports
    });

    [Fact]
    public void Normal_retarget_navigates_to_the_watch_url() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        Assert.True(w.TryRetargetForNewWindow("https://www.youtube.com/watch?v=recVideo001"));

        Assert.StartsWith("https://www.youtube.com/watch?v=recVideo001", w.CurrentUrlForTests);
        Assert.Equal("recVideo001", w.ReturnVideoIdForTests);
    });

    [Fact]
    public void Non_playable_new_window_targets_are_not_retargeted() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();
        var urlBefore = w.CurrentUrlForTests;

        Assert.False(w.TryRetargetForNewWindow("https://www.youtube.com/@SomeChannel"));
        Assert.False(w.TryRetargetForNewWindow("https://example.com/watch?v=dQw4w9WgXcQ"));

        Assert.Equal(urlBefore, w.CurrentUrlForTests);
        Assert.Equal("dQw4w9WgXcQ", w.ReturnVideoIdForTests);   // launch state untouched
    });

    // --- Background room tones (docs/Theme_Preset_Differences.md) ---

    [Fact]
    public void Backgrounds_wear_the_accent_room_tones() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        var res = System.Windows.Application.Current.Resources;

        // Letterbox: the pixels framing the video in BOTH windows ride the derived tint, not Black.
        var letterbox = Assert.IsType<SolidColorBrush>(res["AccentLetterbox"]);
        Assert.NotEqual(Colors.Black, letterbox.Color);   // default intensity carries a faint tone
        Assert.Same(letterbox, ((Grid)w.FindName("SourceLetterbox")!).Background);
        Assert.Same(letterbox, ((Border)w.FindName("SourcePlaceholder")!).Background);
        Assert.Same(letterbox, NewPlayer().Background);

        // Room tone: the main window + toolbar wear the washed background, Settings keeps the flat one.
        var wash = Assert.IsType<SolidColorBrush>(res["AppBackgroundWash"]);
        Assert.Same(wash, w.Background);
        Assert.Same(wash, ((Grid)w.FindName("SourceToolbar")!).Background);
        Assert.NotEqual((Color)res["AppBackgroundColor"], wash.Color);
    });

    [Fact]
    public void Title_wash_sweeps_into_the_washed_background() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        var backdrop = (Border)w.FindName("MainBarBackdrop")!;
        var gradient = Assert.IsType<LinearGradientBrush>(backdrop.Background);
        Assert.Equal(0.80, gradient.GradientStops[1].Offset, 2);
    });

    [Fact]
    public void Profile_rows_wear_their_own_accent_as_a_wash() => StaTestThread.Invoke(() =>
    {
        // docs/Theme_Preset_Differences.md: each dropdown row washes in its OWN accent at the theme's subtle
        // alpha (published as ProfileRowWashAlpha), behind the identity rail.
        var alpha = Assert.IsType<byte>(Application.Current.Resources["ProfileRowWashAlpha"]);

        var window = new MainWindow();
        var combo = (ComboBox)window.FindName("ProfilesCombo")!;
        combo.ItemsSource = new[]
        {
            new Profile
            {
                Name = "Violet",
                Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                AccentColor = "#A78BFA",
            },
        };
        combo.SelectedIndex = 0;
        combo.Measure(new Size(150, 32));
        combo.Arrange(new Rect(0, 0, 150, 32));
        combo.ApplyTemplate();
        combo.UpdateLayout();

        var content = (ContentPresenter)combo.Template.FindName("ContentSite", combo)!;
        content.ApplyTemplate();
        var row = Assert.IsType<Grid>(FindVisualChild(content, "ProfileIdentityRow"));
        row.GetBindingExpression(Grid.BackgroundProperty)?.UpdateTarget();
        Assert.Equal(Color.FromArgb(alpha, 0xA7, 0x8B, 0xFA),
            Assert.IsType<SolidColorBrush>(row.Background).Color);

        combo.ItemsSource = new[]
        {
            new Profile { Name = "Plain", Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" },
        };
        combo.SelectedIndex = 0;
        combo.UpdateLayout();
        content.ApplyTemplate();
        row = Assert.IsType<Grid>(FindVisualChild(content, "ProfileIdentityRow"));
        row.GetBindingExpression(Grid.BackgroundProperty)?.UpdateTarget();
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(row.Background).Color);
    });

    [Fact]
    public void Popout_wears_the_accent_edge() => StaTestThread.Invoke(() =>
    {
        var p = NewPlayer();
        var edge = (Border)p.FindName("PopoutEdgeBorder")!;
        Assert.Equal(new Thickness(1), edge.BorderThickness);
        Assert.Same(Application.Current.Resources["PopoutAccentEdge"], edge.BorderBrush);
    });

    // --- Playlist context in the popout's return identity (spec 22.1: return preserves it) ---

    // --- Return sample vs. identity (review 2026-09-05 PP-04 / PP-01 external retarget) ---

    private static PlayerState SampleOfA() =>
        new(CurrentTime: 42, Paused: false, Duration: 600, Volume: 0.5, Muted: false, PlaybackRate: 1.5);

    [Fact]
    public void Incoming_link_retarget_takes_only_video_targets_on_a_live_player() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();

        w.TrackReturnIdentity("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Assert.False(w.TryRetargetFromIncomingLink(new YouTubeTarget { PlaylistId = "PL0123456789", IsPlaylistOnly = true }));
        Assert.Equal("dQw4w9WgXcQ", w.ReturnVideoIdForTests);

        Assert.True(w.TryRetargetFromIncomingLink(new YouTubeTarget { VideoId = "BBBBBBBBBBB", StartSeconds = 30 }));
        Assert.Equal("BBBBBBBBBBB", w.ReturnVideoIdForTests);
        Assert.Equal("BBBBBBBBBBB", w.CurrentFallbackVideoIdForTests);
        Assert.StartsWith("https://www.youtube.com/watch?v=BBBBBBBBBBB", w.CurrentUrlForTests);
        Assert.Null(w.ReturnSecondsForTests);

        w.Close();
        Assert.True(w.IsClosingForTests);
        Assert.False(w.TryRetargetFromIncomingLink(new YouTubeTarget { VideoId = "CCCCCCCCCCC" }));
        Assert.Equal("BBBBBBBBBBB", w.ReturnVideoIdForTests);
    });

    [Fact]
    public void A_sample_read_for_the_current_video_is_applied() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        var nav = w.NavigationGenerationForTests;
        var identity = w.ReturnIdentityGenerationForTests;

        Assert.True(w.TryApplyReturnPlaybackSampleForTests(SampleOfA(), nav, identity, isFinalCapture: true));

        Assert.Equal(42, w.ReturnSecondsForTests);
        Assert.False(w.ReturnPausedForTests);
        Assert.Equal(0.5, w.ReturnVolumeForTests);
        Assert.Equal(1.5, w.ReturnPlaybackRateForTests);
    });

    [Fact]
    public void A_sample_that_finishes_after_an_incoming_retarget_never_reaches_the_new_video() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        var nav = w.NavigationGenerationForTests;
        var identity = w.ReturnIdentityGenerationForTests;

        Assert.True(w.TryRetargetFromIncomingLink(new YouTubeTarget { VideoId = "BBBBBBBBBBB" }));
        Assert.False(w.TryApplyReturnPlaybackSampleForTests(SampleOfA(), nav, identity, isFinalCapture: true));

        Assert.Equal("BBBBBBBBBBB", w.ReturnVideoIdForTests);
        Assert.Null(w.ReturnSecondsForTests);
        Assert.Null(w.ReturnPausedForTests);
        Assert.Null(w.ReturnVolumeForTests);
        Assert.Null(w.ReturnMutedForTests);
        Assert.Null(w.ReturnPlaybackRateForTests);
    });

    [Fact]
    public void A_sample_that_finishes_after_a_spa_identity_change_never_reaches_the_new_video() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        var nav = w.NavigationGenerationForTests;
        var identity = w.ReturnIdentityGenerationForTests;
        Assert.True(w.TryApplyReturnPlaybackSampleForTests(SampleOfA(), nav, identity, isFinalCapture: false));

        // Autoplay advanced: no NavigationStarting, so the navigation generation is unchanged.
        w.TrackReturnIdentity("https://www.youtube.com/watch?v=BBBBBBBBBBB");
        Assert.Equal(nav, w.NavigationGenerationForTests);
        Assert.NotEqual(identity, w.ReturnIdentityGenerationForTests);

        // The old sample was dropped with the old identity, and a late old read cannot refill it.
        Assert.Null(w.ReturnSecondsForTests);
        Assert.Null(w.ReturnVolumeForTests);
        Assert.False(w.TryApplyReturnPlaybackSampleForTests(SampleOfA(), nav, identity, isFinalCapture: true));
        Assert.Equal("BBBBBBBBBBB", w.ReturnVideoIdForTests);
        Assert.Null(w.ReturnSecondsForTests);
        Assert.Null(w.ReturnPlaybackRateForTests);

        // The same video reported again is not an identity change.
        var after = w.ReturnIdentityGenerationForTests;
        w.TrackReturnIdentity("https://www.youtube.com/watch?v=BBBBBBBBBBB&list=PL0123456789");
        Assert.Equal(after, w.ReturnIdentityGenerationForTests);
    });

    [Fact]
    public void A_failed_capture_and_polls_after_the_final_capture_change_nothing()
    {
        StaTestThread.Invoke(() =>
        {
            var w = NewPlayer();
            var nav = w.NavigationGenerationForTests;
            var identity = w.ReturnIdentityGenerationForTests;

            Assert.False(w.TryApplyReturnPlaybackSampleForTests(null, nav, identity, isFinalCapture: true));
            Assert.Null(w.ReturnSecondsForTests);

            // Headless: no core, so the capture completes synchronously without a sample.
            var returned = w.CaptureReturnStateNowAsync().GetAwaiter().GetResult();
            Assert.Same(returned, w.CaptureReturnStateNowAsync().GetAwaiter().GetResult());
            Assert.False(w.IsSyncTimerRunningForTests);

            // A poll that was in flight when the snapshot froze must not reopen it...
            Assert.False(w.TryApplyReturnPlaybackSampleForTests(SampleOfA(), nav, identity, isFinalCapture: false));
            Assert.Null(w.ReturnSecondsForTests);
            // ...while the final capture itself is the one write that may land on the frozen snapshot.
            Assert.True(w.TryApplyReturnPlaybackSampleForTests(SampleOfA(), nav, identity, isFinalCapture: true));
            Assert.Equal(42, w.ReturnSecondsForTests);
        });
    }

    [Fact]
    public void Popout_source_change_tracks_the_playlist_context() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        w.TrackReturnIdentity("https://www.youtube.com/watch?v=BBBBBBBBBBB&list=PL0123456789");

        Assert.Equal("BBBBBBBBBBB", w.ReturnVideoIdForTests);
        Assert.Equal("PL0123456789", w.ReturnPlaylistIdForTests);
    });

    [Fact]
    public void Popout_source_change_tracks_mix_lists_like_any_playlist() => StaTestThread.Invoke(() =>
    {
        // RD queues advance in the Normal popout; return must follow the mix, not strip it.
        var w = NewPlayer();
        w.TrackReturnIdentity("https://www.youtube.com/watch?v=BBBBBBBBBBB&list=RDdQw4w9WgXcQ");

        Assert.Equal("BBBBBBBBBBB", w.ReturnVideoIdForTests);
        Assert.Equal("RDdQw4w9WgXcQ", w.ReturnPlaylistIdForTests);
    });

    [Fact]
    public void Popout_source_change_clears_the_playlist_when_the_video_leaves_the_list() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        w.TrackReturnIdentity("https://www.youtube.com/watch?v=BBBBBBBBBBB&list=PL0123456789");
        w.TrackReturnIdentity("https://www.youtube.com/watch?v=CCCCCCCCCCC");   // recommendation click

        Assert.Equal("CCCCCCCCCCC", w.ReturnVideoIdForTests);
        Assert.Null(w.ReturnPlaylistIdForTests);
    });

    [Fact]
    public void Popout_source_change_without_a_video_keeps_the_last_known_identity() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        w.TrackReturnIdentity("https://www.youtube.com/watch?v=BBBBBBBBBBB&list=PL0123456789");
        w.TrackReturnIdentity("https://www.youtube.com/feed/library");   // id-hiding page: no evidence of a move

        Assert.Equal("BBBBBBBBBBB", w.ReturnVideoIdForTests);
        Assert.Equal("PL0123456789", w.ReturnPlaylistIdForTests);
    });

    [Fact]
    public void Popout_launch_seeds_the_playlist_context_from_the_target() => StaTestThread.Invoke(() =>
    {
        var w = new PlayerWindow(environment: null!, url: "https://www.youtube.com/playlist?list=PL0123456789",
            topmost: false, placement: null, defaultWidth: 960, defaultHeight: 540, fadeEnabled: true,
            fallbackTarget: new YouTubeTarget { PlaylistId = "PL0123456789", IsPlaylistOnly = true });

        Assert.Null(w.ReturnVideoIdForTests);
        Assert.Equal("PL0123456789", w.ReturnPlaylistIdForTests);
    });

    [Fact]
    public void Retarget_follows_the_new_targets_playlist_context() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        Assert.True(w.TryRetargetForNewWindow("https://www.youtube.com/watch?v=recVideo001&list=PL0123456789"));

        Assert.Equal("recVideo001", w.ReturnVideoIdForTests);
        Assert.Equal("PL0123456789", w.ReturnPlaylistIdForTests);
        Assert.Contains("list=PL0123456789", w.CurrentUrlForTests);
    });

    // --- Video-aware return on the SOURCE side (overhaul Task 3) ---

    [Fact]
    public void Return_to_a_different_video_navigates_and_arms_the_auto_dedup_key() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        w.SeedPopoutReturnForTests(sourceVideoId: "AAAAAAAAAAA");

        w.ApplyReturnActionAsync(new PlayerReturnState
            {
                VideoId = "BBBBBBBBBBB",
                LastKnownSeconds = 42,
                Paused = true,
                Volume = 0.35,
                Muted = true,
                PlaybackRate = 1.25,
            })
            .GetAwaiter().GetResult();   // Navigate completes synchronously (no core to script)

        // The browser never initializes in the test lane, so the navigation queues — the queued
        // URL IS the assertion: the source heads to the RETURNED video at its timestamp.
        Assert.Equal("https://www.youtube.com/watch?v=BBBBBBBBBBB&t=42s", w.PendingUrlForTests);
        // De-dup key armed before navigating: Auto must not instantly re-pop the returned video.
        Assert.Equal("BBBBBBBBBBB", w.AutoLastHandledVideoIdForTests);
        Assert.NotNull(w.PendingReturnReplayForTests);
        Assert.Equal("BBBBBBBBBBB", w.PendingReturnReplayForTests!.VideoId);
        Assert.Equal(42, w.PendingReturnReplayForTests.LastKnownSeconds);
        Assert.True(w.PendingReturnReplayForTests.Paused);
        Assert.Equal(0.35, w.PendingReturnReplayForTests.Volume);
        Assert.True(w.PendingReturnReplayForTests.Muted);
        Assert.Equal(1.25, w.PendingReturnReplayForTests.PlaybackRate);
    });

    [Fact]
    public void Return_to_a_different_video_without_popout_sample_replays_launch_settings() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        w.SeedPopoutReturnForTests(
            sourceVideoId: "AAAAAAAAAAA",
            sourceWasPlayingAtPopout: true,
            sourceVolumeAtPopout: 0.72,
            sourceMutedAtPopout: false,
            sourcePlaybackRateAtPopout: 1.5);

        w.ApplyReturnActionAsync(new PlayerReturnState
            {
                VideoId = "BBBBBBBBBBB",
                LastKnownSeconds = 7,
            })
            .GetAwaiter().GetResult();

        Assert.Equal("https://www.youtube.com/watch?v=BBBBBBBBBBB&t=7s", w.PendingUrlForTests);
        Assert.NotNull(w.PendingReturnReplayForTests);
        Assert.Equal("BBBBBBBBBBB", w.PendingReturnReplayForTests!.VideoId);
        Assert.Equal(7, w.PendingReturnReplayForTests.LastKnownSeconds);
        Assert.False(w.PendingReturnReplayForTests.Paused);
        Assert.Equal(0.72, w.PendingReturnReplayForTests.Volume);
        Assert.False(w.PendingReturnReplayForTests.Muted);
        Assert.Equal(1.5, w.PendingReturnReplayForTests.PlaybackRate);
    });

    [Fact]
    public void Return_on_the_same_video_seeks_rather_than_navigates() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        w.SeedPopoutReturnForTests(sourceVideoId: "AAAAAAAAAAA");

        w.ApplyReturnActionAsync(new PlayerReturnState { VideoId = "AAAAAAAAAAA", LastKnownSeconds = 42 })
            .GetAwaiter().GetResult();

        Assert.Null(w.PendingUrlForTests);               // the seek path scripts a live core instead
        Assert.Equal("AAAAAAAAAAA", w.AutoLastHandledVideoIdForTests);
        Assert.Equal(AutoPopDecision.Skip, AutoPopoutPolicy.Decide(
            autoEnabled: true,
            isPlaying: true,
            isWatchVideo: true,
            currentVideoId: "AAAAAAAAAAA",
            lastHandledVideoId: w.AutoLastHandledVideoIdForTests,
            popoutActive: false));
    });

    [Fact]
    public void Return_to_a_different_video_carries_the_playlist_context() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();
        w.SeedPopoutReturnForTests(sourceVideoId: "AAAAAAAAAAA");

        w.ApplyReturnActionAsync(new PlayerReturnState
            {
                VideoId = "BBBBBBBBBBB",
                PlaylistId = "PL0123456789",
                LastKnownSeconds = 42,
            })
            .GetAwaiter().GetResult();

        // The queue survives the return: the source lands on the playlist's watch URL, not a bare video.
        Assert.Equal("https://www.youtube.com/watch?v=BBBBBBBBBBB&list=PL0123456789&t=42s", w.PendingUrlForTests);
        Assert.NotNull(w.PendingReturnReplayForTests);
        Assert.Equal("PL0123456789", w.PendingReturnReplayForTests!.PlaylistId);
    });

    [Fact]
    public void Playlist_only_launch_returns_by_navigating_to_the_reported_video() => StaTestThread.Invoke(() =>
    {
        // Popped out from a playlist PAGE (no source video id, spec 22.1): the popout started the
        // queue, so returning must follow the user to wherever the queue got to.
        var w = new MainWindow();
        w.SeedPopoutReturnForTests(sourceVideoId: null, popoutLaunchedWithoutVideo: true);

        w.ApplyReturnActionAsync(new PlayerReturnState
            {
                VideoId = "BBBBBBBBBBB",
                PlaylistId = "PL0123456789",
                LastKnownSeconds = 7,
            })
            .GetAwaiter().GetResult();

        Assert.Equal("https://www.youtube.com/watch?v=BBBBBBBBBBB&list=PL0123456789&t=7s", w.PendingUrlForTests);
        Assert.Equal("BBBBBBBBBBB", w.AutoLastHandledVideoIdForTests);   // returned video must not re-pop
    });

    [Fact]
    public void Playlist_only_launch_with_no_reported_video_does_not_navigate() => StaTestThread.Invoke(() =>
    {
        // The popout never left the playlist page: there is nowhere to navigate, the source is
        // already on the very page the user launched from.
        var w = new MainWindow();
        w.SeedPopoutReturnForTests(sourceVideoId: null, popoutLaunchedWithoutVideo: true);

        w.ApplyReturnActionAsync(new PlayerReturnState()).GetAwaiter().GetResult();

        Assert.Null(w.PendingUrlForTests);
    });

    [Fact]
    public void Shell_requests_are_ignored_in_normal_mode() => StaTestThread.Invoke(() =>
    {
        // The shell only exists in compact mode; after the fallback flips the window to normal
        // mode, a late request must be inert (the bridge is disposed, but the guard is belt-and-braces).
        var w = NewPlayer();
        w.HandleShellRequestForTests(Request(PlayerShellProtocol.ActionPinToggle));
        Assert.False(w.Topmost);
    });

    [Fact]
    public void Strip_auto_hide_collapses_after_the_fade_and_activity_restores_it() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactAutoHidePlayer();
        Assert.True(w.StripAutoHideForTests);
        Assert.False(w.IsChromeStripCollapsedForTests);   // visible on construction

        w.HideControlsForTests();
        w.CompleteHideFadeForTests();   // the fade's Completed callback (clocks don't tick headless)
        Assert.True(w.IsChromeStripCollapsedForTests);
        Assert.False(w.IsChromeStripHitTestVisibleForTests);   // Q-8: hidden strip swallows no clicks

        w.OnUserActivityForTests();   // any reveal path must restore the layout row immediately
        Assert.False(w.IsChromeStripCollapsedForTests);
        Assert.True(w.IsChromeStripHitTestVisibleForTests);
    });

    [Fact]
    public void Normal_mode_auto_hide_collapses_and_activity_restores_the_strip() => StaTestThread.Invoke(() =>
    {
        var w = new PlayerWindow(
            environment: null!,
            url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            topmost: false,
            placement: null,
            defaultWidth: 960,
            defaultHeight: 540,
            fadeEnabled: true,
            mode: PlaybackMode.Normal,
            stripAutoHide: true);

        w.HideControlsForTests();
        w.CompleteHideFadeForTests();
        Assert.True(w.IsChromeStripCollapsedForTests);
        Assert.False(w.IsChromeStripHitTestVisibleForTests);

        w.OnUserActivityForTests();
        Assert.False(w.IsChromeStripCollapsedForTests);
        Assert.True(w.IsChromeStripHitTestVisibleForTests);
    });

    [Fact]
    public void Strip_does_not_collapse_when_auto_hide_is_off() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactPlayer();   // fade on, auto-hide off: Stage 4 behavior byte-for-byte
        w.HideControlsForTests();
        w.CompleteHideFadeForTests();

        Assert.False(w.IsChromeStripCollapsedForTests);        // faded, but the row is still reserved
        Assert.False(w.IsChromeStripHitTestVisibleForTests);
    });

    [Fact]
    public void Reveal_mid_fade_keeps_the_strip_interactive_and_uncollapsed() => StaTestThread.Invoke(() =>
    {
        // Activity lands between the hide decision and the fade's completion: the late completion
        // callback must not knock down a strip the user just got back.
        var w = NewCompactAutoHidePlayer();
        w.HideControlsForTests();
        w.OnUserActivityForTests();
        w.CompleteHideFadeForTests();   // the stale callback fires after the reveal

        Assert.False(w.IsChromeStripCollapsedForTests);
        Assert.True(w.IsChromeStripHitTestVisibleForTests);
    });

    [Fact]
    public void Turning_auto_hide_off_restores_a_collapsed_strip() => StaTestThread.Invoke(() =>
    {
        var w = NewCompactAutoHidePlayer();
        w.HideControlsForTests();
        w.CompleteHideFadeForTests();
        Assert.True(w.IsChromeStripCollapsedForTests);   // precondition: collapsed

        // The settings path (MainWindow live re-apply) turns the behavior off mid-collapse.
        w.ApplyAppearance("#00D4FF", 2500, stripAutoHide: false);

        Assert.False(w.StripAutoHideForTests);
        Assert.False(w.IsChromeStripCollapsedForTests);
    });

    [Fact]
    public void Auto_hide_arms_the_activity_probe_and_fade_off_disarms_it() => StaTestThread.Invoke(() =>
    {
        // At 1.0/1.0 opacity defaults the probe used to never run; the auto-hiding strip needs it
        // for the top-edge reveal (WPF sees no mouse over the WebView2 child). Fade off removes
        // the only idleness source, so the probe must stop with it.
        var w = NewCompactAutoHidePlayer();
        _ = new WindowInteropHelper(w).EnsureHandle();   // SourceInitialized applies opacity + probe
        Assert.True(w.IsOpacityHoverPollRunningForTests);

        var fade = (ToggleButton)w.FindName("FadeToggle")!;
        fade.IsChecked = false;
        fade.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.False(w.IsOpacityHoverPollRunningForTests);
        Assert.False(w.IsChromeStripCollapsedForTests);   // fade off pins the strip visible

        fade.IsChecked = true;
        fade.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(w.IsOpacityHoverPollRunningForTests);

        w.Close();
    });

    [Fact]
    public void SettingsWindow_reflects_and_toggles_strip_auto_hide() => StaTestThread.Invoke(() =>
    {
        var on = new SettingsWindow(isBrowserReady: true, stripAutoHideOverride: true);
        Assert.True(on.StripAutoHide);
        Assert.True(((ToggleButton)on.FindName("StripAutoHideToggle")!).IsChecked);

        var w = new SettingsWindow(isBrowserReady: true, stripAutoHideOverride: false);
        Assert.False(w.StripAutoHide);
        var toggle = (ToggleButton)w.FindName("StripAutoHideToggle")!;
        Assert.False(toggle.IsChecked);
        Assert.False(w.AppearanceChanged);   // seeding must not count as a user change

        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(w.StripAutoHide);
        Assert.True(w.AppearanceChanged);
    });

    [Fact]
    public void Profile_mode_picker_hides_dormant_compact_and_round_trips_the_token() => StaTestThread.Invoke(() =>
    {
        // Compact is dormant (PlaybackModePolicy.CompactPlayerEnabled == false): the picker hides the
        // dead "Compact player" option, so a stored compact/embed profile falls back to "Use global".
        Assert.False(PlaybackModePolicy.CompactPlayerEnabled);

        Assert.Equal("normal", Prompt.BuildModePicker("normal").SelectedMode());
        Assert.Null(Prompt.BuildModePicker(null).SelectedMode());
        Assert.Null(Prompt.BuildModePicker("bogus").SelectedMode());     // unknown -> global
        Assert.Null(Prompt.BuildModePicker("compact").SelectedMode());   // dead option hidden -> global
        Assert.Null(Prompt.BuildModePicker("embed").SelectedMode());     // legacy alias likewise

        // Only "Use global default" + "Normal page" are offered; the getter reflects the selection.
        var (element, selectedMode) = Prompt.BuildModePicker(null);
        var combo = (ComboBox)element;
        Assert.Equal(2, combo.Items.Count);
        combo.SelectedIndex = 1;   // "Normal page"
        Assert.Equal("normal", selectedMode());
    });

    [Fact]
    public void Profile_presentation_picker_is_separate_and_round_trips_all_tokens() => StaTestThread.Invoke(() =>
    {
        Assert.Null(Prompt.BuildPresentationPicker(null).SelectedPresentation());
        Assert.Null(Prompt.BuildPresentationPicker("bogus").SelectedPresentation());
        Assert.Equal("standard", Prompt.BuildPresentationPicker("STANDARD").SelectedPresentation());
        Assert.Equal("focused", Prompt.BuildPresentationPicker(" Focused ").SelectedPresentation());

        var (element, selectedPresentation) = Prompt.BuildPresentationPicker(null);
        var combo = (ComboBox)element;
        Assert.Equal(3, combo.Items.Count);

        combo.SelectedIndex = 1;
        Assert.Equal("standard", selectedPresentation());
        combo.SelectedIndex = 2;
        Assert.Equal("focused", selectedPresentation());
    });

    [Fact]
    public void Focused_presentation_hides_native_chrome_only_after_the_overlay_handshake() =>
        StaTestThread.Invoke(() =>
        {
            var focused = new PlayerWindow(
                environment: null!,
                url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                topmost: false,
                placement: null,
                defaultWidth: 960,
                defaultHeight: 540,
                fadeEnabled: true,
                presentation: PopoutPresentation.Focused);

            Assert.Equal(PopoutPresentation.Focused, focused.PresentationForTests);
            Assert.False(focused.IsFocusedSurfaceActiveForTests);
            Assert.False(focused.IsChromeStripCollapsedForTests); // failure-safe until page overlay exists

            focused.ApplyFocusedSurfaceActiveForTests(true);

            Assert.True(focused.IsFocusedSurfaceActiveForTests);
            Assert.True(focused.IsChromeStripCollapsedForTests);
            Assert.False(focused.IsChromeStripHitTestVisibleForTests);
            focused.OnUserActivityForTests();

            Assert.True(focused.IsChromeStripCollapsedForTests);
            Assert.False(focused.IsChromeStripHitTestVisibleForTests);

            focused.BeginNavigationForTests();
            Assert.False(focused.IsFocusedSurfaceActiveForTests);
            Assert.False(focused.IsChromeStripCollapsedForTests); // every navigation restores recovery chrome

            Assert.Equal(PopoutPresentation.Standard, NewPlayer().PresentationForTests);
        });

    [Fact]
    public void Surface_drag_handoff_is_single_shot_and_native_loop_owns_drag_lifecycle() =>
        StaTestThread.Invoke(() =>
        {
            var w = new PlayerWindow(
                environment: null!,
                url: "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                topmost: false,
                placement: null,
                defaultWidth: 960,
                defaultHeight: 540,
                fadeEnabled: true,
                stripAutoHide: true);
            w.HideControlsForTests();
            w.CompleteHideFadeForTests();
            Assert.True(w.IsChromeStripCollapsedForTests);

            Assert.Throws<InvalidOperationException>(() =>
                w.ExecuteSurfaceDragRequestForTests(leftButtonDown: true, () =>
                {
                    // Posting SC_MOVE is only a queued handoff; the native loop has not started yet.
                    Assert.False(w.IsDraggingForTests);
                    throw new InvalidOperationException("native move failed");
                }));

            Assert.False(w.IsDraggingForTests);
            Assert.False(w.IsSurfaceDragQueuedForTests);
            Assert.True(w.IsChromeStripCollapsedForTests);

            w.OnUserActivityForTests();
            Assert.True(w.IsIdleTimerRunningForTests);
            w.HideControlsForTests();
            w.CompleteHideFadeForTests();
            w.HandleNativeMoveSizeStateChangedForTests(active: true);
            Assert.True(w.IsDraggingForTests);
            Assert.False(w.IsIdleTimerRunningForTests);
            Assert.True(w.IsChromeStripCollapsedForTests);

            w.HandleNativeMoveSizeStateChangedForTests(active: false);
            Assert.False(w.IsDraggingForTests);
            Assert.True(w.IsIdleTimerRunningForTests);
            Assert.True(w.IsChromeStripCollapsedForTests);
        });

    [Fact]
    public void Surface_drag_is_inert_when_maximized_or_the_button_is_up() => StaTestThread.Invoke(() =>
    {
        var w = NewPlayer();
        var nativeMoves = 0;

        w.ExecuteSurfaceDragRequestForTests(leftButtonDown: false, () => nativeMoves++);
        w.WindowState = WindowState.Maximized;
        w.ExecuteSurfaceDragRequestForTests(leftButtonDown: true, () => nativeMoves++);

        Assert.Equal(0, nativeMoves);
        Assert.False(w.IsSurfaceDragQueuedForTests);
        Assert.False(w.IsDraggingForTests);
    });

    [Fact]
    public void Native_move_size_messages_drive_player_activity_and_destroy_cleanup() =>
        StaTestThread.Invoke(() =>
        {
            var w = NewPlayer();
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            Assert.True(BorderlessWindowHelper.HasExpandedResizeSubclassForTests(hwnd));

            w.OnUserActivityForTests();
            Assert.True(w.IsIdleTimerRunningForTests);
            _ = SendMessage(hwnd, WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
            Assert.True(w.IsDraggingForTests);
            Assert.False(w.IsIdleTimerRunningForTests);

            _ = SendMessage(hwnd, WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
            Assert.False(w.IsDraggingForTests);
            Assert.True(w.IsIdleTimerRunningForTests);

            _ = SendMessage(hwnd, WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
            Assert.True(w.IsDraggingForTests);
            w.Close();

            Assert.False(w.IsDraggingForTests);
            Assert.False(w.IsIdleTimerRunningForTests);
            Assert.False(BorderlessWindowHelper.HasExpandedResizeSubclassForTests(hwnd));
        });

    // --- Dark theme is actually wired at runtime (rebuts the stale "renders light" reports) ---

    [Fact]
    public void Dark_theme_styles_are_applied_at_runtime() => StaTestThread.Invoke(() =>
    {
        var w = new MainWindow();

        // The chrome controls use the dark styles (not platform-default light ones).
        Assert.Same(Application.Current.TryFindResource("DarkComboBox"),
            ((ComboBox)w.FindName("ProfilesCombo")!).Style);
        Assert.Same(Application.Current.TryFindResource("DarkTextBox"),
            ((TextBox)w.FindName("UrlBox")!).Style);

        // The app-wide implicit ToolTip style exists and is dark (Background = SurfaceRaised).
        // The style references the THEMED brush via DynamicResource (so a theme change restyles
        // open tooltips), so resolve it through a realized ToolTip rather than casting the
        // Setter.Value (which is now a DynamicResourceExtension, not a brush).
        var toolTipStyle = Application.Current.TryFindResource(typeof(ToolTip)) as Style;
        Assert.NotNull(toolTipStyle);
        var tip = new ToolTip { Style = toolTipStyle };
        var surfaceRaised = (SolidColorBrush)Application.Current.FindResource("SurfaceRaised");
        var bg = Assert.IsType<SolidColorBrush>(tip.Background);
        Assert.Equal(surfaceRaised.Color, bg.Color);
    });

    // --- DPI characterization: URL text is not clipped to a band at 150% DPI ---

    [Fact]
    public void UrlText_is_not_clipped_to_a_band_at_150pct_dpi() => StaTestThread.Invoke(() =>
    {
        const double dpi = 144; // 150%
        // FEAS-01: measure the host UNCONSTRAINED vertically so the arranged field height is DRIVEN by
        // DarkTextBox.MinHeight = DensityControlHeight — the dense end (30 DIP, below the old hardcoded
        // 32). A fixed-height host masked clipping by re-stretching the field to 32 regardless of the
        // token, so lowering DensityControlHeight rendered an identical 32 px field — the gate was
        // invariant to the value it claimed to police.
        var expectedHeight = (double)Application.Current.Resources["DensityControlHeight"];
        var box = new TextBox
        {
            Text = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            Width = 320,
            Style = (Style)Application.Current.FindResource("DarkTextBox"),
        };
        var host = new Border { Background = Brushes.Black, Child = box, UseLayoutRounding = false };
        using var presentationSource = new HwndSource(new HwndSourceParameters("PiPlay URL text render test")
        {
            Width = 320,
            Height = (int)Math.Ceiling(expectedHeight),
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP without WS_VISIBLE
        }) { RootVisual = host };

        host.Measure(new Size(320, double.PositiveInfinity));   // unconstrained height: MinHeight drives it
        var h = host.DesiredSize.Height;
        host.Arrange(new Rect(0, 0, 320, h));
        host.UpdateLayout();

        // The arranged field height now IS the density token — proving the gate exercises it (not a
        // fixed 32 px box). If a future DensityControlHeight regression shrank the field, the inked-row
        // check below would catch the clipping at this real dense height.
        Assert.Equal(expectedHeight, box.ActualHeight);

        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(320 * dpi / 96), (int)Math.Ceiling(h * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(host);

        var inkedRows = CountInkedRows(rtb);
        Assert.True(inkedRows >= 8, $"Only {inkedRows} inked rows — text appears clipped to a band.");
    });

    // Count horizontal scanlines containing a bright (text-ink) pixel. Text is #F3F5F7 on dark.
    private static int CountInkedRows(RenderTargetBitmap rtb)
    {
        var w = rtb.PixelWidth;
        var h = rtb.PixelHeight;
        var stride = w * 4;
        var px = new byte[h * stride];
        rtb.CopyPixels(px, stride, 0);

        var rows = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * stride + x * 4; // Pbgra32: B,G,R,A
                if (px[i] > 80 || px[i + 1] > 80 || px[i + 2] > 80) { rows++; break; }
            }
        }
        return rows;
    }

    private static FrameworkElement? FindVisualChild(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            var nested = FindVisualChild(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private static IntPtr MakeLParam(int x, int y) =>
        new(unchecked((x & 0xffff) | ((y & 0xffff) << 16)));

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Layer 3 constructs real windows (never shown). Close any that remain on the shared STA
    // thread after each test so they don't accumulate on Application.Windows for the whole run.
    public void Dispose() => StaTestThread.Invoke(() =>
    {
        foreach (var w in Application.Current.Windows.Cast<Window>().ToArray())
        {
            try { w.Close(); } catch { /* a never-shown window may resist closing; ignore */ }
        }
    });
}
