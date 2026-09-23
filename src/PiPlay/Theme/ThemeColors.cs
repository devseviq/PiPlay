using System.Globalization;
using System.Windows.Media;

namespace PiPlay.Theme;

/// <summary>
/// Pure color helpers for the theme resource layer (overhaul Task 9). Turns a normalized accent hex
/// (<see cref="ThemeCatalog.NormalizeAccentColor"/>) into WPF brushes/colors and derives the lighter
/// hover variant. No WPF dispatcher affinity: <see cref="Color"/> is a struct and the math is
/// thread-agnostic; only <see cref="Brush"/> allocates a Freezable (frozen before return so it is
/// safe to share across the source window and popout).
/// </summary>
public static class ThemeColors
{
    private const double MinimumPressedStateContrast = 1.10;

    /// <summary>
    /// The title-bar wash at accent intensity 0 — a contrast ratio of 1.0 against <c>SurfaceBase</c> is
    /// the surface itself, i.e. no wash at all. Intensity 0 means OFF, not "a bit less".
    /// </summary>
    internal const double ShellTintContrastFloor = 1.00;

    /// <summary>
    /// The title-bar wash at accent intensity 100. Above this it stops being a tint and becomes a
    /// saturated banner, which re-adds the heavy "framed" look P1 exists to remove — so this is a hard
    /// ceiling, pinned by <c>ThemeColorsTests</c>, not a suggestion.
    /// </summary>
    internal const double ShellTintContrastCeiling = 1.90;

    /// <summary>Parse a <c>#RRGGBB</c> hex (the normalized accent form) into an opaque color.</summary>
    public static Color ParseColor(string? hex)
    {
        var value = ThemeCatalog.NormalizeAccentColor(hex)[1..];   // normalize guarantees 6 upper-hex
        var r = byte.Parse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = byte.Parse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = byte.Parse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromRgb(r, g, b);
    }

    /// <summary>Blend a color toward white by <paramref name="amount"/> (0..1) for a hover/light variant.</summary>
    public static Color Lighten(Color color, double amount)
    {
        var t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromRgb(
            Mix(color.R, 255, t),
            Mix(color.G, 255, t),
            Mix(color.B, 255, t));
    }

    /// <summary>A frozen exact-color brush for picker swatches and other raw-color previews.</summary>
    public static SolidColorBrush Brush(string? hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// A frozen presentation brush that preserves <paramref name="hex"/> when it is already visible
    /// against <paramref name="adjacent"/>, otherwise applying the minimum contrast correction.
    /// Stored/picker colors continue to use <see cref="Brush"/> and remain exact.
    /// </summary>
    public static SolidColorBrush ContrastBrush(string? hex, Color adjacent, double minimumRatio = 3.0)
    {
        var brush = new SolidColorBrush(EnsureContrast(ParseColor(hex), adjacent, minimumRatio));
        brush.Freeze();
        return brush;
    }

    private static byte Mix(byte from, byte to, double t) =>
        (byte)Math.Round(from + (to - from) * t);

    /// <summary>Blend two colors channel-wise by <paramref name="amount"/> (0..1, clamped).</summary>
    public static Color Mix(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromRgb(Mix(from.R, to.R, t), Mix(from.G, to.G, t), Mix(from.B, to.B, t));
    }

    /// <summary>The same RGB at a new alpha — for the subtle/glow accent overlay tokens.</summary>
    public static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>
    /// WCAG 2.x relative-luminance contrast ratio between two opaque colors. Deliberately the SAME
    /// formula as the test oracle (<c>tests/PiPlay.Tests/Infrastructure/Wcag.cs</c>) so the runtime
    /// foreground choice agrees with the contrast gates that police it.
    /// </summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var (la, lb) = (RelativeLuminance(a), RelativeLuminance(b));
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// Preserve <paramref name="color"/> byte-for-byte when it already meets the requested contrast.
    /// Otherwise binary-search the smallest mix toward whichever black/white pole contrasts better
    /// with <paramref name="adjacent"/>. This is a presentation transform only; callers persist the
    /// original normalized RGB value.
    /// </summary>
    public static Color EnsureContrast(Color color, Color adjacent, double minimumRatio = 3.0)
    {
        var minimum = Math.Max(1.0, minimumRatio);
        if (ContrastRatio(color, adjacent) >= minimum) return color;

        var pole = ContrastRatio(Colors.White, adjacent) >= ContrastRatio(Colors.Black, adjacent)
            ? Colors.White
            : Colors.Black;
        if (ContrastRatio(pole, adjacent) < minimum) return pole;

        return MixTowardContrast(color, pole, adjacent, minimum);
    }

    /// <summary>
    /// Return the smallest mix from <paramref name="from"/> toward <paramref name="toward"/> that
    /// reaches <paramref name="minimumRatio"/> against <paramref name="adjacent"/>.
    /// </summary>
    public static Color MixTowardContrast(
        Color from, Color toward, Color adjacent, double minimumRatio)
    {
        var minimum = Math.Max(1.0, minimumRatio);
        if (ContrastRatio(from, adjacent) >= minimum) return from;
        if (ContrastRatio(toward, adjacent) < minimum) return toward;

        var low = 0.0;
        var high = 1.0;
        for (var i = 0; i < 24; i++)
        {
            var mid = (low + high) / 2.0;
            if (ContrastRatio(Mix(from, toward, mid), adjacent) >= minimum)
                high = mid;
            else
                low = mid;
        }

        return Mix(from, toward, high);
    }

    private static double RelativeLuminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte v)
    {
        var s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary>The dark button-text foreground used on light accents (theme-v2 spec).</summary>
    private static readonly Color DarkForeground = Color.FromRgb(0x06, 0x14, 0x1A);

    /// <summary>
    /// Pick the best dark/white foreground for <paramref name="accent"/>. Most colors clear WCAG AA
    /// with one of these; mid-tone colors can fall in the mathematical dead zone, where neither does.
    /// In that case return the higher-contrast candidate instead of rejecting the user's color.
    /// </summary>
    public static Color PickReadableForeground(Color accent)
    {
        var darkContrast = ContrastRatio(DarkForeground, accent);
        var whiteContrast = ContrastRatio(Colors.White, accent);
        return darkContrast >= whiteContrast ? DarkForeground : Colors.White;
    }

    /// <summary>The theme-v2 accent derivation profile for a theme id (spec "Suggested profiles").</summary>
    public static ThemeAccentProfile AccentProfileFor(string? themeId) =>
        ThemeCatalog.NormalizeThemeId(themeId) switch
        {
            "minimal"    => new ThemeAccentProfile(0.22, 0.14, 0.50, 0.12, 0x26, 0x40),
            "soft-glass" => new ThemeAccentProfile(0.30, 0.12, 0.40, 0.16, 0x33, 0x66),
            _            => new ThemeAccentProfile(0.18, 0.16, 0.58, 0.10, 0x22, 0x33),   // sharp-dark / default
        };

    /// <summary>Full-dial mix ceilings for the background room tones (docs/Theme_Preset_Differences.md).</summary>
    private const double LetterboxMixCeiling = 0.06;
    private const double BackgroundWashMixCeiling = 0.04;

    /// <summary>
    /// Alpha of the checked-toggle wash (polish review 2026-09-10 F-7): the presentation accent laid
    /// over the toggle's own surface so a checked Pin/Auto/preset chip is a filled shape, not only a
    /// recoloured glyph or hairline. Fixed rather than dial-driven because it carries STATE, which
    /// must survive intensity 0 the way the primary action's fill does. 0x2A keeps every catalog
    /// accent's glyph at or above 3:1 on the washed base/raised surfaces of every preset while moving
    /// the surface by more than the 1.10:1 pressed-state delta (ThemeCatalogTests).
    /// </summary>
    internal const byte CheckedWashAlpha = 0x2A;

    /// <summary>
    /// Derive the accent state tokens for one base accent under a theme (theme-v2 "Derivation
    /// algorithm"). Each theme reads the same base accent differently via its
    /// <see cref="ThemeAccentProfile"/> and its own raised surface.
    /// </summary>
    public static DerivedAccentSet DeriveAccentSet(
        string? baseAccent, ThemePreset preset, int? accentIntensity = null)
    {
        var profile = AccentProfileFor(preset.Id);
        var requested = ParseColor(baseAccent);
        var surfaceBase = ParseColor(preset.Palette.SurfaceBase);
        var surfaceRaised = ParseColor(preset.Palette.SurfaceRaised);
        var surfaceHover = ParseColor(preset.Palette.SurfaceHover);
        // How far the accent reaches into the chrome (user-set). The wash uses the full dial; glyphs
        // finish their fade-in at the midpoint so default 50 reproduces v0.9.0 exactly.
        var reach = ThemeCatalog.NormalizeAccentIntensity(accentIntensity) / 100.0;
        var glyphReach = Math.Min(reach * 2.0, 1.0);
        // REQ-UI-01 / spec section 17: arbitrary stored colors remain exact, while the presentation
        // token clears the 3:1 non-text contrast floor on the lightest shipped dark interaction surface.
        var primary = EnsureContrast(requested, surfaceHover);

        var hover = Mix(primary, Colors.White, profile.HoverWhiteMix);
        var darkerPressed = EnsureContrast(
            Mix(primary, Colors.Black, profile.PressedBlackMix), surfaceHover);
        // Very dark stored colors are lifted to the minimum 3:1 presentation floor. Darkening that
        // boundary color and correcting it back to 3:1 can land on the same RGB as Primary, erasing
        // the pressed state. Keep the normal darker recipe whenever it remains visible; only that
        // collapsed edge case takes the smallest lighter step that clears a 1.10:1 state delta.
        var pressed = ContrastRatio(primary, darkerPressed) >= MinimumPressedStateContrast
            ? darkerPressed
            : MixTowardContrast(primary, Colors.White, primary, MinimumPressedStateContrast);
        var muted = Mix(primary, surfaceRaised, profile.MutedSurfaceMix);
        var border = Mix(primary, Colors.White, profile.BorderWhiteMix);
        var subtle = WithAlpha(primary, profile.SubtleAlpha);
        var glow = WithAlpha(primary, profile.GlowAlpha);
        // Decorative shell tint: visibly carries the accent into the title bar without becoming a
        // hard line, full fill, or another control boundary. The user's intensity dial scales it from
        // "no wash at all" up to the banner ceiling.
        var shellTarget = ShellTintContrastFloor
                          + (ShellTintContrastCeiling - ShellTintContrastFloor) * reach;
        var shellTint = MixTowardContrast(surfaceBase, primary, surfaceBase, shellTarget);

        // Toolbar chrome glyphs: neutral text at intensity 0, the full accent by 50. Above 50 only the
        // wash deepens. Re-run EnsureContrast on the BLEND because a midpoint between two individually
        // legible colors is not automatically legible - without this, the toolbar could disappear.
        var chromeGlyph = EnsureContrast(
            Mix(ParseColor(preset.Palette.TextPrimary), primary, glyphReach), surfaceHover);

        var onAccent = PickReadableForeground(primary);
        // CON-1: re-pick the foreground against the DARKER pressed fill, not reuse OnAccent — a dim
        // accent (steel) may need to flip to white.
        var onAccentPressed = PickReadableForeground(pressed);

        // Background room tones (docs/Theme_Preset_Differences.md): the dial also reaches the letterbox framing
        // the video and the window background, as faint tints — 0 keeps them accent-free (pure
        // black / the flat palette background), and the ceilings keep them tones, never colors.
        var appBackground = ParseColor(preset.Palette.AppBackground);
        var letterbox = Mix(Colors.Black, primary, LetterboxMixCeiling * reach);
        var backgroundWash = Mix(appBackground, primary, BackgroundWashMixCeiling * reach);
        // The active-profile popout edge: the Border tone alpha-scaled by the dial, so 0 keeps the
        // popout chrome-free and 100 draws the full identity line.
        var popoutEdge = WithAlpha(border, (byte)Math.Round(0xFF * reach));
        var checkedWash = WithAlpha(primary, CheckedWashAlpha);

        return new DerivedAccentSet(
            primary, hover, pressed, muted, border, subtle, glow, shellTint, chromeGlyph,
            onAccent, onAccentPressed, letterbox, backgroundWash, popoutEdge, checkedWash);
    }
}

/// <summary>
/// Per-theme recipe for turning one base accent into the derived accent state tokens (theme-v2
/// spec "Derivation algorithm"). The same base accent reads differently per theme because each
/// preset mixes it toward white/black/its own raised surface by different amounts.
/// </summary>
public sealed record ThemeAccentProfile(
    double HoverWhiteMix,
    double PressedBlackMix,
    double MutedSurfaceMix,
    double BorderWhiteMix,
    byte SubtleAlpha,
    byte GlowAlpha);

/// <summary>
/// The derived accent state colors for one (base accent x theme) pairing. A pure value set — the
/// spec forbids persisting derived colors; the applier turns these into frozen brushes at apply time.
/// </summary>
public sealed record DerivedAccentSet(
    Color Primary,
    Color Hover,
    Color Pressed,
    Color Muted,
    Color Border,
    Color Subtle,
    Color Glow,
    Color ShellTint,
    /// <summary>Toolbar glyph color: ordinary text at accent intensity 0, the full accent from 50 up.</summary>
    Color ChromeGlyph,
    Color OnAccent,
    Color OnAccentPressed,
    /// <summary>Near-black room tint framing the video (docs/Theme_Preset_Differences.md); pure black at intensity 0.</summary>
    Color Letterbox,
    /// <summary>The window background washed faintly toward the accent; the flat palette value at intensity 0.</summary>
    Color BackgroundWash,
    /// <summary>The popout's 1px identity edge: Border alpha-scaled by the dial; transparent at 0.</summary>
    Color PopoutEdge,
    /// <summary>Checked-toggle fill: Primary at <see cref="ThemeColors.CheckedWashAlpha"/>, independent of the dial.</summary>
    Color CheckedWash);
