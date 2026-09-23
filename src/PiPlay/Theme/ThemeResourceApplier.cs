using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PiPlay.Models;

namespace PiPlay.Theme;

/// <summary>
/// Applies the resolved theme (accent + per-preset palette + corner radii) to the app's shared
/// resources. Every theme-controlled consumer references these keys via <c>DynamicResource</c>, so
/// REPLACING the dictionary entries re-resolves every consumer — including controls already
/// realized in open windows. (Compiled BAML freezes the seed brushes, so mutating them in place is
/// a no-op; replacing the dictionary entry is the mechanism that actually works — overhaul Task 9.)
/// Call at <c>App.OnStartup</c> for the persisted theme and again whenever it changes for a live
/// restyle. Native window corners are NOT applied here — windows apply
/// <see cref="ThemePreferenceResolver.DwmCorners"/> to their own HWND.
/// </summary>
public static class ThemeResourceApplier
{
    /// <summary>
    /// The native corner preference of the last applied theme, for windows built outside the
    /// settings flow (the Prompt dialogs) — they have no ThemeSettings at hand but must wear the
    /// same corner shape as their owners. Updated by every <see cref="Apply"/>.
    /// </summary>
    public static DwmCornerMode CurrentDwmCorners { get; private set; } = DwmCornerMode.Default;

    /// <summary>The palette brush keys this applier owns, in palette order. Shared with tests so
    /// the applied-key set and the catalog palette cannot drift.</summary>
    public static readonly string[] PaletteBrushKeys =
    [
        "AppBackground", "SurfaceBase", "SurfaceRaised", "SurfaceHover",
        "BorderSubtle", "BorderStrong", "TextPrimary", "TextSecondary", "DangerPin",
    ];

    public static void Apply(ResourceDictionary resources, ThemeSettings? theme, PlayerSettings player)
    {
        var preset = ThemeCatalog.PresetFor(theme?.ThemeId);
        ApplyAccentOnly(
            resources, ThemePreferenceResolver.AccentColor(theme, player), preset, theme?.AccentIntensity);
        ApplyPalette(resources, preset.Palette);
        ApplyRadii(resources, ThemeCatalog.RadiiFor(preset, theme?.CornerStyle));
        ApplyDensity(resources, preset.Density);
        ApplyElevation(resources, preset.Elevation);
        CurrentDwmCorners = ThemeCatalog.DwmCornersFor(preset, theme?.CornerStyle);
    }

    /// <summary>
    /// Derive the accent state set for the resolved (accent x theme) and keep every accent token
    /// AND its companion <c>*Color</c> entry in step (review BL-09 — the companions used to go stale).
    /// Changed entries are replaced so <c>DynamicResource</c> consumers re-resolve; equal frozen
    /// brushes/colors retain identity so intensity preview does not invalidate unrelated consumers.
    /// OnAccentPressed carries the CON-1 fix: a dim accent's pressed fill gets a readable foreground
    /// rather than the reused OnAccent.
    /// </summary>
    public static void ApplyAccentOnly(
        ResourceDictionary resources, string accentColor, ThemePreset preset, int? accentIntensity = null)
    {
        var set = ThemeColors.DeriveAccentSet(accentColor, preset, accentIntensity);
        SetColorPair(resources, "AccentPrimary", set.Primary);
        SetColorPair(resources, "AccentHover", set.Hover);
        SetColorPair(resources, "AccentPressed", set.Pressed);
        SetColorPair(resources, "AccentBorder", set.Border);
        SetColorPair(resources, "AccentShellTint", set.ShellTint);
        // The accent-reach dial's other half: toolbar glyphs ride this, not AccentPrimary, so intensity 0
        // returns them to ordinary text color instead of leaving them stuck on the accent.
        SetColorPair(resources, "AccentChromeGlyph", set.ChromeGlyph);
        SetColorPair(resources, "OnAccent", set.OnAccent);
        // Hover can cross the dark/white threshold just as pressed can.
        SetColorPair(resources, "OnAccentHover", ThemeColors.PickReadableForeground(set.Hover));
        SetColorPair(resources, "OnAccentPressed", set.OnAccentPressed);
        // Checked-toggle wash (polish review 2026-09-10 F-7): the accent at a fixed alpha, dial-independent.
        SetColorPair(resources, "AccentCheckedWash", set.CheckedWash);
        // A checked glyph sits above the wash, including over the lighter hover surface. Correct
        // its ink against that composite rather than the unwashed surface used by ordinary icons.
        var checkedHover = ThemeColors.Mix(ThemeColors.ParseColor(preset.Palette.SurfaceHover),
            set.CheckedWash, set.CheckedWash.A / 255.0);
        // WPF's premultiplied-byte composition can differ from the ideal blend by a channel step;
        // retain a little headroom so the rendered glyph still clears the 3:1 non-text floor.
        SetColorPair(resources, "AccentCheckedGlyph", ThemeColors.EnsureContrast(set.Primary, checkedHover, 3.1));
        // Keep AccentPrimaryLight defined as an alias to AccentHover for one migration pass.
        SetColorPair(resources, "AccentPrimaryLight", set.Hover);
        // Background room tones (docs/Theme_Preset_Differences.md): the letterbox framing the video and the
        // washed window background follow the accent and the intensity dial.
        SetColorPair(resources, "AccentLetterbox", set.Letterbox);
        SetColorPair(resources, "AppBackgroundWash", set.BackgroundWash);
        SetColorPair(resources, "PopoutAccentEdge", set.PopoutEdge);
        // The profile dropdown row wash borrows the theme's subtle alpha (each row supplies its
        // OWN accent, so only the alpha travels through resources).
        if (resources["ProfileRowWashAlpha"] is not byte alpha || alpha != set.Subtle.A)
            resources["ProfileRowWashAlpha"] = set.Subtle.A;
    }

    private static void SetColorPair(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is not SolidColorBrush brush || brush.Color != color || !brush.IsFrozen)
            resources[key] = Frozen(color);

        var colorKey = key + "Color";
        if (resources[colorKey] is not Color existingColor || existingColor != color)
            resources[colorKey] = color;   // companion Color token, kept in step
    }

    private static void ApplyPalette(ResourceDictionary resources, ThemePalette palette)
    {
        string[] values =
        [
            palette.AppBackground, palette.SurfaceBase, palette.SurfaceRaised, palette.SurfaceHover,
            palette.BorderSubtle, palette.BorderStrong, palette.TextPrimary, palette.TextSecondary,
            palette.Danger,
        ];
        for (var i = 0; i < PaletteBrushKeys.Length; i++)
        {
            var color = ThemeColors.ParseColor(values[i]);
            resources[PaletteBrushKeys[i]] = Frozen(color);
            // Keep the companion Color token in step for any direct color consumer.
            resources[PaletteBrushKeys[i] + "Color"] = color;
        }
        // Danger-fill text follows the accent's readability policy per preset (polish review
        // 2026-09-10 F-1): a derived pair, not a palette entry, so it never needs hand-tuning.
        SetColorPair(resources, "OnDanger", ThemeColors.PickReadableForeground(ThemeColors.ParseColor(palette.Danger)));
        // Brighten the fill instead of fading the entire button and its label.
        SetColorPair(resources, "DangerHover", ThemeColors.Lighten(ThemeColors.ParseColor(palette.Danger), 0.12));
    }

    private static void ApplyRadii(ResourceDictionary resources, ThemeRadii radii)
    {
        resources["RadiusMainWindowFrame"] = new CornerRadius(radii.MainWindowFrame);
        resources["RadiusPopoutFrame"] = new CornerRadius(radii.PopoutFrame);
        // Title bars round only their top corners so they sit flush on the content below.
        resources["RadiusTitleBar"] = new CornerRadius(radii.TitleBar, radii.TitleBar, 0, 0);
        resources["RadiusButton"] = new CornerRadius(radii.Button);
        resources["RadiusIconButton"] = new CornerRadius(radii.IconButton);
        resources["RadiusInput"] = new CornerRadius(radii.Input);
        resources["RadiusPanel"] = new CornerRadius(radii.Panel);
        resources["RadiusPopup"] = new CornerRadius(radii.Popup);
        resources["RadiusThumbnail"] = new CornerRadius(radii.Thumbnail);
        resources["RadiusSwatch"] = new CornerRadius(radii.Swatch);
        resources["RadiusScrollbarThumb"] = new CornerRadius(radii.ScrollbarThumb);
        resources["RadiusToolTip"] = new CornerRadius(radii.ToolTip);
        // Compatibility aliases (docs/Theme_Preset_Differences.md) for one migration pass; kept as separate
        // entries, not resource-to-resource references.
        resources["ControlCornerRadius"] = new CornerRadius(radii.Input);
        resources["ButtonCornerRadius"] = new CornerRadius(radii.Button);
    }

    /// <summary>
    /// Replace the density resources from the preset's <see cref="ThemeDensity"/>. Heights/sizes land
    /// as <see cref="double"/>; paddings and the uniform default border land as <see cref="Thickness"/>
    /// (the struct type Padding/BorderThickness DynamicResource consumers expect — a double/string there
    /// hits the .NET 10 DynamicResource type-mismatch crash class). Replaced, not mutated, like the
    /// palette/radius/accent entries above.
    /// </summary>
    private static void ApplyDensity(ResourceDictionary resources, ThemeDensity density)
    {
        resources["DensityControlHeight"] = density.ControlHeight;
        resources["DensityIconButtonSize"] = density.IconButtonSize;
        resources["DensityScrollbarThickness"] = density.ScrollbarThickness;
        resources["DensityButtonPadding"] = density.ButtonPadding;
        resources["DensityInputPadding"] = density.InputPadding;
        resources["DensityMenuItemPadding"] = density.MenuItemPadding;
        resources["DensityPresetChipPadding"] = density.PresetChipPadding;
        resources["DensityToolTipPadding"] = density.ToolTipPadding;
        resources["BorderThicknessDefault"] = density.BorderThicknessDefault;
    }

    /// <summary>
    /// Replace the inner-elevation effects from the preset's <see cref="ThemeElevation"/>. A null
    /// <paramref name="elevation"/> (Sharp Dark) writes a null Effect so popup/panel consumers render
    /// flat — NOT a no-op DropShadowEffect, which would still cost per-frame raster. Minimal and Soft
    /// Glass get frozen <see cref="DropShadowEffect"/>s. Inner-only: these feed popup/menu/panel Effects,
    /// never an outer window (the windows host WebView2 by HWND and stay AllowsTransparency=False).
    /// </summary>
    private static void ApplyElevation(ResourceDictionary resources, ThemeElevation? elevation)
    {
        resources["ElevationPopup"] = elevation is null
            ? null
            : FrozenShadow(elevation.PopupBlurRadius, elevation.PopupShadowDepth, elevation.PopupOpacity);
        resources["ElevationPanel"] = elevation is null
            ? null
            : FrozenShadow(elevation.PanelBlurRadius, elevation.PanelShadowDepth, elevation.PanelOpacity);
    }

    private static DropShadowEffect FrozenShadow(double blurRadius, double shadowDepth, double opacity)
    {
        var effect = new DropShadowEffect
        {
            BlurRadius = blurRadius,
            ShadowDepth = shadowDepth,
            Opacity = opacity,
            Color = Colors.Black,   // neutral inner shadow; Direction stays the WPF default (315°)
        };
        effect.Freeze();   // shareable + immutable across windows, like the brushes above
        return effect;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();   // shareable + immutable; DynamicResource consumers hold it until the next Apply
        return brush;
    }
}
