using System.Windows;
using System.Windows.Media;
using PiPlay.Theme;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class ThemeCatalogTests
{
    [Fact]
    public void Presets_have_unique_stable_ids_and_required_first_pass_options()
    {
        var ids = ThemeCatalog.Presets.Select(p => p.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("sharp-dark", ids);
        Assert.Contains("minimal", ids);
        Assert.Contains("soft-glass", ids);
        Assert.All(ids, id => Assert.Equal(id, ThemeCatalog.NormalizeThemeId(id.ToUpperInvariant())));
    }

    [Fact]
    public void Accent_options_store_normalized_hex_colors()
    {
        var colors = ThemeCatalog.AccentOptions.Select(o => o.HexColor).ToArray();

        Assert.Equal(colors.Length, colors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(colors, color =>
        {
            Assert.Equal(color, ThemeCatalog.NormalizeAccentColor(color));
            Assert.StartsWith("#", color);
            Assert.Equal(7, color.Length);
        });
    }

    [Theory]
    [InlineData("#a78bfa", "#A78BFA")]
    [InlineData("38d996", "#38D996")]
    [InlineData("not-a-color", "#2BAED0")]
    [InlineData("#12345", "#2BAED0")]
    public void Accent_hex_is_normalized_or_reset_to_default(string input, string expected)
    {
        Assert.Equal(expected, ThemeCatalog.NormalizeAccentColor(input));
    }

    [Theory]
    [InlineData("#00D4FF", true)]
    [InlineData("#abcdef", true)]
    [InlineData("00D4FF", true)]
    [InlineData("not-a-color", false)]
    [InlineData("#12345", false)]
    [InlineData("#1234567", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsValidHex_matches_the_canonical_normalizer(string? input, bool expected)
    {
        Assert.Equal(expected, ThemeCatalog.IsValidHex(input));
    }

    [Theory]
    [InlineData("cyan", "#2BAED0")]
    [InlineData("violet", "#9E84F0")]
    [InlineData("green", "#2DB57F")]
    [InlineData("amber", "#D69A2E")]
    [InlineData("bogus", "#2BAED0")]
    public void Legacy_accent_keys_map_to_hex_seed_values(string legacyAccent, string expected)
    {
        Assert.Equal(expected, ThemeCatalog.AccentColorForLegacyAccent(legacyAccent));
    }

    [Theory]
    [InlineData(1500, "short")]
    [InlineData(2500, "normal")]
    [InlineData(4000, "long")]
    [InlineData(777, "normal")]
    public void Fade_delay_preset_maps_existing_millisecond_values(int milliseconds, string expected)
    {
        Assert.Equal(expected, ThemeCatalog.FadeDelayPresetForMilliseconds(milliseconds));
        Assert.Equal(milliseconds == 777 ? 2500 : milliseconds, ThemeCatalog.FadeDelayMillisecondsForPreset(expected));
    }

    // --- Visual token sets: radii + native corner mode (docs/Theme_Preset_Differences.md) ---

    private static IEnumerable<double> AllRadii(ThemeRadii r) =>
        [r.MainWindowFrame, r.PopoutFrame, r.TitleBar, r.Button, r.IconButton, r.Input,
         r.Panel, r.Popup, r.Thumbnail, r.Swatch, r.ScrollbarThumb, r.ToolTip];

    [Fact]
    public void Preset_radii_are_sane_and_ordered_by_softness()
    {
        foreach (var preset in ThemeCatalog.Presets)
        {
            Assert.All(AllRadii(preset.Radii), v =>
                Assert.True(v is >= 0 and <= 24, $"{preset.Id}: radius {v} outside 0..24."));
            Assert.True(Enum.IsDefined(preset.DwmCorners), $"{preset.Id}: undefined DwmCornerMode.");
        }

        // Soft Glass is the floating overlay theme: its popout frame is its roundest window shape.
        var softGlass = ThemeCatalog.PresetFor("soft-glass");
        Assert.True(softGlass.Radii.PopoutFrame >= softGlass.Radii.MainWindowFrame);

        // Theme personality ordering (docs/Theme_Preset_Differences.md): sharp <= minimal <= soft-glass per token.
        var sharp = AllRadii(ThemeCatalog.PresetFor("sharp-dark").Radii).ToArray();
        var minimal = AllRadii(ThemeCatalog.PresetFor("minimal").Radii).ToArray();
        var soft = AllRadii(softGlass.Radii).ToArray();
        for (var i = 0; i < sharp.Length; i++)
        {
            Assert.True(sharp[i] <= minimal[i], $"sharp token {i} ({sharp[i]}) above minimal ({minimal[i]}).");
            Assert.True(minimal[i] <= soft[i], $"minimal token {i} ({minimal[i]}) above soft-glass ({soft[i]}).");
        }
    }

    [Theory]
    [InlineData(null, "theme")]
    [InlineData("", "theme")]
    [InlineData("THEME", "theme")]
    [InlineData("Square", "square")]
    [InlineData("small", "small")]
    [InlineData("soft", "round")]   // "soft" is a legacy alias for "round" (deduped 2026-06)
    [InlineData("round", "round")]
    [InlineData("bogus", "theme")]
    public void Corner_style_normalizes_to_the_catalog_vocabulary(string? input, string expected)
    {
        Assert.Equal(expected, ThemeCatalog.NormalizeCornerStyle(input));
        Assert.Contains(expected, ThemeCatalog.CornerStyleOptions.Select(o => o.Key));
    }

    [Fact]
    public void Corner_style_override_swaps_the_whole_profile_for_every_preset()
    {
        foreach (var preset in ThemeCatalog.Presets)
        {
            // "theme" follows the preset; the named styles replace radii AND native mode together.
            Assert.Equal(preset.Radii, ThemeCatalog.RadiiFor(preset, "theme"));
            Assert.Equal(preset.DwmCorners, ThemeCatalog.DwmCornersFor(preset, "theme"));

            Assert.All(AllRadii(ThemeCatalog.RadiiFor(preset, "square")), v => Assert.Equal(0, v));
            Assert.Equal(DwmCornerMode.Square, ThemeCatalog.DwmCornersFor(preset, "square"));

            Assert.Equal(ThemeCatalog.PresetFor("sharp-dark").Radii, ThemeCatalog.RadiiFor(preset, "small"));
            Assert.Equal(DwmCornerMode.SmallRound, ThemeCatalog.DwmCornersFor(preset, "small"));

            // "soft" is a legacy alias for "round" (both were always DWMWCP_ROUND): it resolves
            // identically to "round" now — same radii and native corner.
            Assert.Equal(ThemeCatalog.RadiiFor(preset, "round"), ThemeCatalog.RadiiFor(preset, "soft"));
            Assert.Equal(ThemeCatalog.DwmCornersFor(preset, "round"), ThemeCatalog.DwmCornersFor(preset, "soft"));

            Assert.Equal(ThemeCatalog.PresetFor("soft-glass").Radii, ThemeCatalog.RadiiFor(preset, "round"));
            Assert.Equal(DwmCornerMode.Round, ThemeCatalog.DwmCornersFor(preset, "round"));
        }
    }

    [Fact]
    public void Default_theme_leaves_native_corners_untouched()
    {
        // The pristine-window guarantee: a fresh install (sharp-dark, corner style "theme") must
        // not write any DWM corner preference, keeping default windows byte-identical.
        Assert.Equal(DwmCornerMode.Default,
            ThemeCatalog.DwmCornersFor(ThemeCatalog.PresetFor(ThemeCatalog.DefaultThemeId), ThemeCatalog.DefaultCornerStyle));
    }

    // --- Accent switch rule (docs/Theme_Preset_Differences.md) ---

    [Theory]
    [InlineData("#2BAED0", "sharp-dark", "soft-glass", "#9E84F0")]   // on previous default → adopt next default
    [InlineData("#2baed0", "sharp-dark", "soft-glass", "#9E84F0")]   // lowercase normalizes before comparison
    [InlineData("#FFC857", "sharp-dark", "soft-glass", "#FFC857")]   // custom accent survives the switch
    [InlineData("#ffc857", "sharp-dark", "soft-glass", "#FFC857")]   // …and comes back normalized
    [InlineData("#9E84F0", "soft-glass", "sharp-dark", "#2BAED0")]   // works in both directions
    public void Accent_for_theme_switch_preserves_custom_accents(
        string current, string fromTheme, string toTheme, string expected)
    {
        Assert.Equal(expected, ThemeCatalog.AccentForThemeSwitch(
            current, ThemeCatalog.PresetFor(fromTheme), ThemeCatalog.PresetFor(toTheme)));
    }

    // --- Per-preset palette readability (docs/Theme_Preset_Differences.md), same Wcag gates as the
    // Colors.xaml seed theories in XamlInvariantTests but across EVERY preset palette. ---

    public static IEnumerable<object[]> PresetIds() =>
        ThemeCatalog.Presets.Select(p => new object[] { p.Id });

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void Preset_palettes_meet_contrast_minimums(string presetId)
    {
        var p = ThemeCatalog.PresetFor(presetId).Palette;

        Assert.True(Wcag.ContrastRatio(p.TextPrimary, p.AppBackground) >= 4.5, $"{presetId}: TextPrimary on AppBackground.");
        Assert.True(Wcag.ContrastRatio(p.TextPrimary, p.SurfaceBase) >= 4.5, $"{presetId}: TextPrimary on SurfaceBase.");
        Assert.True(Wcag.ContrastRatio(p.TextPrimary, p.SurfaceRaised) >= 4.5, $"{presetId}: TextPrimary on SurfaceRaised.");
        Assert.True(Wcag.ContrastRatio(p.TextSecondary, p.SurfaceBase) >= 4.5, $"{presetId}: TextSecondary on SurfaceBase.");

        // DangerButton text is OnDanger, picked by contrast per preset like OnAccent (polish review
        // F-1): white never met 4.5:1 on either Danger (3.43:1 rose, 3.58:1 warm), dark ink does.
        var onDanger = Hex(ThemeColors.PickReadableForeground(ThemeColors.ParseColor(p.Danger)));
        var dangerText = Wcag.ContrastRatio(onDanger, p.Danger);
        Assert.True(dangerText >= 4.5, $"{presetId}: OnDanger {onDanger} on Danger = {dangerText:F2}:1.");

        // Every offered accent stays readable on every preset's hover surface (glyph gate) and
        // keeps carrying the dark AccentButton text (fill gate).
        foreach (var option in ThemeCatalog.AccentOptions)
        {
            var glyph = Wcag.ContrastRatio(option.HexColor, p.SurfaceHover);
            Assert.True(glyph >= 3.0, $"{presetId}: accent {option.Key} on hover surface = {glyph:F2}:1.");
            var text = Wcag.ContrastRatio("#FF06141A", option.HexColor);
            Assert.True(text >= 4.5, $"{presetId}: dark button text on accent {option.Key} = {text:F2}:1.");

            // Checked toggles (polish review F-7) paint the accent wash behind the glyph/label on the
            // base and raised surfaces: the glyph must still read on the washed surface, and the wash
            // must move the surface by at least the pressed-state delta the accent derivation enforces.
            var set = ThemeColors.DeriveAccentSet(option.HexColor, ThemeCatalog.PresetFor(presetId));
            foreach (var surface in new[] { p.SurfaceBase, p.SurfaceRaised })
            {
                var washed = Hex(ThemeColors.Mix(ThemeColors.ParseColor(surface), set.CheckedWash, set.CheckedWash.A / 255.0));
                var washedGlyph = Wcag.ContrastRatio(Hex(set.Primary), washed);
                Assert.True(washedGlyph >= 3.0, $"{presetId}: accent {option.Key} on its checked wash over {surface} = {washedGlyph:F2}:1.");
                var delta = Wcag.ContrastRatio(surface, washed);
                Assert.True(delta >= 1.10, $"{presetId}: checked wash of {option.Key} over {surface} moves it only {delta:F2}:1.");
            }
        }
    }

    // --- TG-2 / TG-10: exact-value gates (docs/Theme_Preset_Differences.md) ---
    // These pin the spec's target tables as HARDCODED literals, NOT derived from the catalog. The
    // existing XamlInvariantTests seed test only proves Colors.xaml ↔ catalog consistency; changing
    // the catalog and that test together stays green and enforces nothing against the spec. These
    // gates fail if any shipped catalog value drifts from the agreed v2 target, independently.

    private static void AssertPresetMatchesSpec(
        string id, string displayName, string defaultAccent, string fadeDelay, bool stripAutoHide,
        double activeOpacity, double idleOpacity, DwmCornerMode dwm, ThemePalette palette, ThemeRadii radii)
    {
        var p = ThemeCatalog.PresetFor(id);
        Assert.Equal(id, p.Id);
        Assert.Equal(displayName, p.DisplayName);
        Assert.Equal(defaultAccent, p.DefaultAccentColor);
        Assert.Equal(fadeDelay, p.DefaultFadeDelayPreset);
        Assert.Equal(stripAutoHide, p.DefaultStripAutoHide);
        Assert.Equal(activeOpacity, p.DefaultActiveWindowOpacity);
        Assert.Equal(idleOpacity, p.DefaultIdleWindowOpacity);
        Assert.Equal(dwm, p.DwmCorners);
        Assert.Equal(palette, p.Palette);   // record equality covers all nine surface/text tokens
        Assert.Equal(radii, p.Radii);       // record equality covers all twelve semantic radii
    }

    [Fact]
    public void Sharp_dark_matches_the_v2_spec_literals() => AssertPresetMatchesSpec(
        "sharp-dark", "Sharp Dark", "#2BAED0", "normal", stripAutoHide: true,
        activeOpacity: 1.0, idleOpacity: 1.0, DwmCornerMode.Default,
        new ThemePalette(
            AppBackground: "#050609", SurfaceBase: "#0B0E12", SurfaceRaised: "#131820",
            SurfaceHover: "#1E2630", BorderSubtle: "#181F29", BorderStrong: "#262F3D",
            TextPrimary: "#F4F7FA", TextSecondary: "#9AA2AD", Danger: "#E45D75"),
        new ThemeRadii(
            MainWindowFrame: 2, PopoutFrame: 2, TitleBar: 2, Button: 3, IconButton: 3, Input: 3,
            Panel: 2, Popup: 4, Thumbnail: 2, Swatch: 4, ScrollbarThumb: 3, ToolTip: 4));

    [Fact]
    public void Minimal_matches_the_v2_spec_literals() => AssertPresetMatchesSpec(
        "minimal", "Minimal", "#3F84C0", "long", stripAutoHide: true,
        activeOpacity: 0.94, idleOpacity: 0.86, DwmCornerMode.SmallRound,
        new ThemePalette(
            AppBackground: "#14120F", SurfaceBase: "#1C1A16", SurfaceRaised: "#26231E",
            SurfaceHover: "#312D27", BorderSubtle: "#2E2A23", BorderStrong: "#3C362D",
            TextPrimary: "#F4F1EC", TextSecondary: "#B0A99E", Danger: "#E8564C"),
        new ThemeRadii(
            MainWindowFrame: 8, PopoutFrame: 12, TitleBar: 8, Button: 8, IconButton: 8, Input: 8,
            Panel: 10, Popup: 10, Thumbnail: 6, Swatch: 8, ScrollbarThumb: 5, ToolTip: 8));

    [Fact]
    public void Soft_glass_matches_the_v2_spec_literals() => AssertPresetMatchesSpec(
        "soft-glass", "Soft Glass", "#9E84F0", "short", stripAutoHide: true,
        activeOpacity: 0.82, idleOpacity: 0.72, DwmCornerMode.Round,
        new ThemePalette(
            AppBackground: "#0B1018", SurfaceBase: "#121A26", SurfaceRaised: "#1B2738",
            SurfaceHover: "#26354B", BorderSubtle: "#2A3A52", BorderStrong: "#3A4D6A",
            TextPrimary: "#F6F8FC", TextSecondary: "#C4CEDC", Danger: "#E45D75"),
        new ThemeRadii(
            MainWindowFrame: 14, PopoutFrame: 22, TitleBar: 14, Button: 12, IconButton: 12, Input: 12,
            Panel: 16, Popup: 16, Thumbnail: 10, Swatch: 12, ScrollbarThumb: 6, ToolTip: 10));

    [Fact]
    public void Theme_palette_and_radii_use_structural_equality()
    {
        // The *_matches_the_v2_spec_literals gates above pin all nine palette tokens and twelve radii
        // through Assert.Equal(expectedRecord, preset.Palette/Radii) — which only compares per-field
        // because ThemePalette/ThemeRadii are records. If either were ever refactored to a class,
        // those asserts would silently degrade to reference equality and stop enforcing the literals.
        // This guard turns that refactor red instead of letting it gut the spec gates unnoticed.
        Assert.Equal(
            new ThemePalette("#1", "#2", "#3", "#4", "#5", "#6", "#7", "#8", "#9"),
            new ThemePalette("#1", "#2", "#3", "#4", "#5", "#6", "#7", "#8", "#9"));
        Assert.NotEqual(
            new ThemePalette("#1", "#2", "#3", "#4", "#5", "#6", "#7", "#8", "#9"),
            new ThemePalette("#0", "#2", "#3", "#4", "#5", "#6", "#7", "#8", "#9"));
        Assert.Equal(
            new ThemeRadii(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12),
            new ThemeRadii(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12));
        Assert.NotEqual(
            new ThemeRadii(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12),
            new ThemeRadii(0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12));
    }

    // --- Task 2 / TG-1: anti-collapse identity gates. The ordering test guarantees only <=, which a
    // regression collapsing presets back to near-identical values would still pass. These assert the
    // presets stay visibly distinct on the axes v2 newly diverges. ---

    [Fact]
    public void Preset_identities_stay_visually_distinct()
    {
        var sharp = ThemeCatalog.PresetFor("sharp-dark");
        var minimal = ThemeCatalog.PresetFor("minimal");
        var soft = ThemeCatalog.PresetFor("soft-glass");

        // Popout-frame rounding strictly increases sharp < minimal < soft-glass.
        Assert.True(sharp.Radii.PopoutFrame < minimal.Radii.PopoutFrame,
            $"sharp PopoutFrame {sharp.Radii.PopoutFrame} not below minimal {minimal.Radii.PopoutFrame}.");
        Assert.True(minimal.Radii.PopoutFrame < soft.Radii.PopoutFrame,
            $"minimal PopoutFrame {minimal.Radii.PopoutFrame} not below soft-glass {soft.Radii.PopoutFrame}.");

        // Soft Glass popout reads as the overlay-soft shape: at least 16 DIP rounder than Sharp.
        Assert.True(soft.Radii.PopoutFrame - sharp.Radii.PopoutFrame >= 16,
            $"soft-glass PopoutFrame only {soft.Radii.PopoutFrame - sharp.Radii.PopoutFrame} DIP above sharp.");

        // Native corner mode is distinct per preset: Sharp pristine, Minimal small, Soft Glass round.
        Assert.Equal(DwmCornerMode.Default, sharp.DwmCorners);
        Assert.Equal(DwmCornerMode.SmallRound, minimal.DwmCorners);
        Assert.Equal(DwmCornerMode.Round, soft.DwmCorners);
        Assert.Equal(3, new[] { sharp.DwmCorners, minimal.DwmCorners, soft.DwmCorners }.Distinct().Count());

        // Each preset ships a distinct default accent identity.
        Assert.Equal(3, new[]
        {
            sharp.DefaultAccentColor, minimal.DefaultAccentColor, soft.DefaultAccentColor,
        }.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Preset_behavior_defaults_stay_differentiated()
    {
        var sharp = ThemeCatalog.PresetFor("sharp-dark");
        var minimal = ThemeCatalog.PresetFor("minimal");
        var soft = ThemeCatalog.PresetFor("soft-glass");

        // Fade delay diverges across all three. It is NON-MONOTONIC (normal/long/short =
        // 2500/4000/1500), so an ordering inequality would be wrong — assert pairwise distinctness.
        Assert.Equal(3, new[]
        {
            sharp.DefaultFadeDelayPreset, minimal.DefaultFadeDelayPreset, soft.DefaultFadeDelayPreset,
        }.Distinct(StringComparer.Ordinal).Count());

        // Fade reclaims the top-bar row by default under every preset. The explicit override still
        // lets a user reserve it, but a preset-following install never keeps a blank strip.
        Assert.True(soft.DefaultStripAutoHide);
        Assert.True(sharp.DefaultStripAutoHide);
        Assert.True(minimal.DefaultStripAutoHide);

        // Opaque -> quiet -> glass is a strict, visible behavior axis. Every value remains inside
        // the owner's 0-30% transparency direction (>= 0.70).
        Assert.Equal(1.0, sharp.DefaultActiveWindowOpacity);
        Assert.Equal(1.0, sharp.DefaultIdleWindowOpacity);
        Assert.True(sharp.DefaultActiveWindowOpacity > minimal.DefaultActiveWindowOpacity);
        Assert.True(minimal.DefaultActiveWindowOpacity > soft.DefaultActiveWindowOpacity);
        Assert.True(sharp.DefaultIdleWindowOpacity > minimal.DefaultIdleWindowOpacity);
        Assert.True(minimal.DefaultIdleWindowOpacity > soft.DefaultIdleWindowOpacity);
        Assert.True(soft.DefaultIdleWindowOpacity >= 0.70);
    }

    // --- CON-1 (theme-v2 Phase B): derived accent tokens stay WCAG-safe in their PINNED pairings,
    // across every offered accent x every theme profile (see docs/Theme_Preset_Differences.md).
    // The naive derivation reused OnAccent on the darker pressed fill, dropping the dim steel chip to
    // 3.82:1; OnAccentPressed is re-picked against the pressed fill so it stays readable. Uses the
    // independent Wcag oracle (not the production ContrastRatio it polices). AccentMuted/Subtle/Glow
    // are contextual/alpha tokens with no consumer in this pass — gated when a consumer lands, per the
    // spec's conditional-on-use rule (and AccentMuted has a known light-text gap on bright minimal/
    // soft-glass chips that needs a design decision before it ships). ---

    public static IEnumerable<object[]> AccentByPreset() =>
        from preset in ThemeCatalog.Presets
        from accent in ThemeCatalog.AccentOptions
        select new object[] { preset.Id, accent.Key };

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    [Theory]
    [MemberData(nameof(AccentByPreset))]
    public void Derived_accent_tokens_meet_contrast_minimums(string presetId, string accentKey)
    {
        var preset = ThemeCatalog.PresetFor(presetId);
        var accent = ThemeCatalog.AccentOptions.Single(o => o.Key == accentKey).HexColor;
        var set = ThemeColors.DeriveAccentSet(accent, preset);

        // OnAccent must read on both the primary fill and the (lighter) hover fill.
        var onPrimary = Wcag.ContrastRatio(Hex(set.OnAccent), Hex(set.Primary));
        Assert.True(onPrimary >= 4.5, $"{presetId}/{accentKey}: OnAccent on AccentPrimary = {onPrimary:F2}:1.");
        var onHover = Wcag.ContrastRatio(Hex(set.OnAccent), Hex(set.Hover));
        Assert.True(onHover >= 4.5, $"{presetId}/{accentKey}: OnAccent on AccentHover = {onHover:F2}:1.");

        // CON-1: OnAccentPressed must read on the DARKER pressed fill. Steel on soft-glass is the
        // tightest pair at 4.52:1 (white foreground) — only ~0.02 above the floor — so a future
        // PressedBlackMix increase for any dim accent must not silently push it under 4.5 here.
        var onPressed = Wcag.ContrastRatio(Hex(set.OnAccentPressed), Hex(set.Pressed));
        Assert.True(onPressed >= 4.5, $"{presetId}/{accentKey}: OnAccentPressed on AccentPressed = {onPressed:F2}:1 (CON-1).");

        // AccentBorder is a focus/checked outline drawn on the dark surfaces: UI-component 3.0:1.
        var borderOnBase = Wcag.ContrastRatio(Hex(set.Border), preset.Palette.SurfaceBase);
        Assert.True(borderOnBase >= 3.0, $"{presetId}/{accentKey}: AccentBorder on SurfaceBase = {borderOnBase:F2}:1.");
        var borderOnRaised = Wcag.ContrastRatio(Hex(set.Border), preset.Palette.SurfaceRaised);
        Assert.True(borderOnRaised >= 3.0, $"{presetId}/{accentKey}: AccentBorder on SurfaceRaised = {borderOnRaised:F2}:1.");
    }

    // --- TG-3: density + elevation exact-value gates (docs/Theme_Preset_Differences.md).
    // Like the palette/radii literals above, these pin the canonical values
    // target tables as HARDCODED literals, independent of the catalog. TG-3: a <= ordering gate is NOT
    // enough — it passes when all three presets collapse to the identical "Safe fallback" column, the
    // exact "Sharp compact / Soft Glass airy" regression the density pass exists to prevent — and
    // strict < is wrong because several axes legitimately tie (ScrollbarThickness 8/10/10,
    // BorderThicknessDefault 1/1/1, several padding components). So: exact literals per preset PLUS a
    // strict-distinctness check on ONLY the axes the spec actually diverges. ---

    private static void AssertDensityMatchesSpec(
        string id, double controlHeight, double iconButtonSize, double scrollbarThickness,
        Thickness buttonPadding, Thickness inputPadding, Thickness menuItemPadding,
        Thickness presetChipPadding, Thickness toolTipPadding, Thickness borderThicknessDefault)
    {
        var d = ThemeCatalog.PresetFor(id).Density;
        Assert.Equal(controlHeight, d.ControlHeight);
        Assert.Equal(iconButtonSize, d.IconButtonSize);
        Assert.Equal(scrollbarThickness, d.ScrollbarThickness);
        Assert.Equal(buttonPadding, d.ButtonPadding);
        Assert.Equal(inputPadding, d.InputPadding);
        Assert.Equal(menuItemPadding, d.MenuItemPadding);
        Assert.Equal(presetChipPadding, d.PresetChipPadding);
        Assert.Equal(toolTipPadding, d.ToolTipPadding);
        Assert.Equal(borderThicknessDefault, d.BorderThicknessDefault);
    }

    [Fact]
    public void Sharp_dark_density_matches_the_v2_spec_literals() => AssertDensityMatchesSpec(
        "sharp-dark", controlHeight: 30, iconButtonSize: 30, scrollbarThickness: 8,
        buttonPadding: new Thickness(10, 5, 10, 5), inputPadding: new Thickness(8, 0, 8, 0),
        menuItemPadding: new Thickness(8, 5, 8, 5), presetChipPadding: new Thickness(8, 0, 8, 0),
        toolTipPadding: new Thickness(7, 4, 7, 4), borderThicknessDefault: new Thickness(1));

    [Fact]
    public void Minimal_density_matches_the_v2_spec_literals() => AssertDensityMatchesSpec(
        "minimal", controlHeight: 34, iconButtonSize: 32, scrollbarThickness: 10,
        buttonPadding: new Thickness(12, 6, 12, 6), inputPadding: new Thickness(10, 0, 10, 0),
        menuItemPadding: new Thickness(10, 6, 10, 6), presetChipPadding: new Thickness(10, 0, 10, 0),
        toolTipPadding: new Thickness(8, 5, 8, 5), borderThicknessDefault: new Thickness(1));

    [Fact]
    public void Soft_glass_density_matches_the_v2_spec_literals() => AssertDensityMatchesSpec(
        "soft-glass", controlHeight: 38, iconButtonSize: 36, scrollbarThickness: 10,
        buttonPadding: new Thickness(16, 9, 16, 9), inputPadding: new Thickness(14, 2, 14, 2),
        menuItemPadding: new Thickness(14, 9, 14, 9), presetChipPadding: new Thickness(14, 0, 14, 0),
        toolTipPadding: new Thickness(10, 7, 10, 7), borderThicknessDefault: new Thickness(1));

    [Fact]
    public void Density_diverges_on_the_axes_that_must_and_ties_the_uniform_axes()
    {
        var sharp = ThemeCatalog.PresetFor("sharp-dark").Density;
        var minimal = ThemeCatalog.PresetFor("minimal").Density;
        var soft = ThemeCatalog.PresetFor("soft-glass").Density;

        // The "compact -> airy" axes increase STRICTLY (Sharp compact, Soft Glass airy). A <= gate
        // would pass on a collapse to one shared value, so assert strict < on the axes that diverge.
        Assert.True(sharp.ControlHeight < minimal.ControlHeight && minimal.ControlHeight < soft.ControlHeight,
            $"ControlHeight not strictly increasing: {sharp.ControlHeight}/{minimal.ControlHeight}/{soft.ControlHeight}.");
        Assert.True(sharp.IconButtonSize < minimal.IconButtonSize && minimal.IconButtonSize < soft.IconButtonSize,
            $"IconButtonSize not strictly increasing: {sharp.IconButtonSize}/{minimal.IconButtonSize}/{soft.IconButtonSize}.");
        // Horizontal button padding (the most visible control) opens up Sharp < Minimal < Soft Glass.
        Assert.True(sharp.ButtonPadding.Left < minimal.ButtonPadding.Left && minimal.ButtonPadding.Left < soft.ButtonPadding.Left,
            $"ButtonPadding horizontal not strictly increasing: {sharp.ButtonPadding.Left}/{minimal.ButtonPadding.Left}/{soft.ButtonPadding.Left}.");

        // Scrollbar thickens off Sharp but Minimal/Soft Glass intentionally TIE (8/10/10): assert that
        // exact shape, never an ordering a collapse could satisfy.
        Assert.True(sharp.ScrollbarThickness < minimal.ScrollbarThickness, "Scrollbar must thicken off Sharp.");
        Assert.Equal(minimal.ScrollbarThickness, soft.ScrollbarThickness);

        // BorderThicknessDefault is a uniform 1 across all three this pass: border weight is not a v2
        // differentiation axis until pixel-snapping/layout-rounding risk has its own gate.
        Assert.Equal(new Thickness(1), sharp.BorderThicknessDefault);
        Assert.Equal(sharp.BorderThicknessDefault, minimal.BorderThicknessDefault);
        Assert.Equal(minimal.BorderThicknessDefault, soft.BorderThicknessDefault);
    }

    [Fact]
    public void Sharp_dark_has_no_inner_elevation()
    {
        // Sharp is the flat utility shell: no inner popup/panel shadow at all. The applier writes a
        // literal null Effect, never a no-op DropShadowEffect (which would still cost per-frame raster).
        Assert.Null(ThemeCatalog.PresetFor("sharp-dark").Elevation);
    }

    [Fact]
    public void Minimal_elevation_matches_the_v2_spec_literals()
    {
        var e = ThemeCatalog.PresetFor("minimal").Elevation;
        Assert.NotNull(e);
        Assert.Equal(8, e!.PopupBlurRadius);
        Assert.Equal(1, e.PopupShadowDepth);
        Assert.Equal(0.22, e.PopupOpacity);
        Assert.Equal(6, e.PanelBlurRadius);
        Assert.Equal(1, e.PanelShadowDepth);
        Assert.Equal(0.16, e.PanelOpacity);
    }

    [Fact]
    public void Soft_glass_elevation_matches_the_v2_spec_literals()
    {
        var e = ThemeCatalog.PresetFor("soft-glass").Elevation;
        Assert.NotNull(e);
        Assert.Equal(16, e!.PopupBlurRadius);
        Assert.Equal(2, e.PopupShadowDepth);
        Assert.Equal(0.34, e.PopupOpacity);
        Assert.Equal(12, e.PanelBlurRadius);
        Assert.Equal(2, e.PanelShadowDepth);
        Assert.Equal(0.26, e.PanelOpacity);
    }

    [Fact]
    public void Inner_elevation_strengthens_from_minimal_to_soft_glass()
    {
        // Sharp has none; Soft Glass is the airy overlay shell, so every elevation axis is at least as
        // strong as Minimal's and the blur (the most visible axis) is strictly stronger.
        var minimal = ThemeCatalog.PresetFor("minimal").Elevation!;
        var soft = ThemeCatalog.PresetFor("soft-glass").Elevation!;
        Assert.True(soft.PopupBlurRadius > minimal.PopupBlurRadius, "Soft Glass popup blur must exceed Minimal.");
        Assert.True(soft.PanelBlurRadius > minimal.PanelBlurRadius, "Soft Glass panel blur must exceed Minimal.");
        Assert.True(soft.PopupOpacity > minimal.PopupOpacity && soft.PanelOpacity > minimal.PanelOpacity,
            "Soft Glass shadow opacity must exceed Minimal.");
    }

    [Fact]
    public void Theme_density_and_elevation_use_structural_equality()
    {
        // The same guard as Theme_palette_and_radii_use_structural_equality: the density/elevation
        // *_matches_the_v2_spec_literals gates lean on per-field record equality. If ThemeDensity or
        // ThemeElevation were refactored to a class, Assert.Equal would silently degrade to reference
        // equality and stop enforcing the literals. Keep that refactor red.
        Assert.Equal(
            new ThemeDensity(30, 30, 8, new Thickness(10, 5, 10, 5), new Thickness(8, 0, 8, 0),
                new Thickness(8, 5, 8, 5), new Thickness(8, 0, 8, 0), new Thickness(7, 4, 7, 4), new Thickness(1)),
            new ThemeDensity(30, 30, 8, new Thickness(10, 5, 10, 5), new Thickness(8, 0, 8, 0),
                new Thickness(8, 5, 8, 5), new Thickness(8, 0, 8, 0), new Thickness(7, 4, 7, 4), new Thickness(1)));
        Assert.NotEqual(
            new ThemeDensity(30, 30, 8, new Thickness(10, 5, 10, 5), new Thickness(8, 0, 8, 0),
                new Thickness(8, 5, 8, 5), new Thickness(8, 0, 8, 0), new Thickness(7, 4, 7, 4), new Thickness(1)),
            new ThemeDensity(34, 30, 8, new Thickness(10, 5, 10, 5), new Thickness(8, 0, 8, 0),
                new Thickness(8, 5, 8, 5), new Thickness(8, 0, 8, 0), new Thickness(7, 4, 7, 4), new Thickness(1)));
        Assert.Equal(
            new ThemeElevation(8, 1, 0.22, 6, 1, 0.16),
            new ThemeElevation(8, 1, 0.22, 6, 1, 0.16));
        Assert.NotEqual(
            new ThemeElevation(8, 1, 0.22, 6, 1, 0.16),
            new ThemeElevation(16, 1, 0.22, 6, 1, 0.16));
    }
}
