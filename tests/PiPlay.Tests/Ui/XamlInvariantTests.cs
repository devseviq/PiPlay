using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using PiPlay.Services;
using PiPlay.Theme;

namespace PiPlay.Tests;

/// <summary>
/// Layer 1 — markup invariants. Parses the source .xaml as XML (no WPF runtime) and asserts the
/// burned-in properties that, if silently flipped, break the app — most notably the
/// <c>UseLayoutRounding="False"</c> guard that re-catches the "rounding = 0" URL-text clipping.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Markup)]
public class XamlInvariantTests
{
    // Segoe Fluent / MDL2 glyphs live in the Unicode Private Use Area (U+E000 and up).
    private const int PuaStart = 0xE000;

    private static XElement Window(string file) => XamlTestFiles.Load(file).Root!;
    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    // --- Window layout / airspace / chrome ---

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("PlayerWindow.xaml")]
    [InlineData("SettingsWindow.xaml")]
    public void Window_layout_and_airspace_invariants_hold(string file)
    {
        var w = Window(file);

        // The "rounding = 0" regression: layout rounding MUST be off on every window (UI-CHK-5).
        Assert.Equal("False", Attr(w, "UseLayoutRounding"));
        // WebView2 airspace hard constraint (ADR-0004): a transparent window breaks the HwndHost.
        Assert.Equal("False", Attr(w, "AllowsTransparency"));
        // Custom chrome + crisp scaling.
        Assert.Equal("None", Attr(w, "WindowStyle"));
        Assert.Equal("True", Attr(w, "SnapsToDevicePixels"));
    }

    [Theory]
    [InlineData("MainWindow.xaml", "42")]
    [InlineData("PlayerWindow.xaml", "0")]
    public void WindowChrome_invariants_hold(string file, string expectedCaptionHeight)
    {
        var chrome = Window(file).Descendants(XamlTestFiles.Pres + "WindowChrome").Single();

        Assert.Equal("0", chrome.Attribute("CornerRadius")?.Value);
        Assert.Equal("0", chrome.Attribute("GlassFrameThickness")?.Value);
        Assert.Equal("False", chrome.Attribute("UseAeroCaptionButtons")?.Value);
        Assert.Equal(BorderlessResizeHitTestPolicy.ResizeBorderDip.ToString(),
            chrome.Attribute("ResizeBorderThickness")?.Value);
        Assert.Equal(expectedCaptionHeight, chrome.Attribute("CaptionHeight")?.Value);
    }

    // --- WebView2 resize inset band (REQ-WINDOW-02, overhaul Task 1) ---

    [Theory]
    [InlineData("MainWindow.xaml", "Browser")]
    [InlineData("PlayerWindow.xaml", "Player")]
    public void WebView_margin_gives_the_window_the_resize_band(string file, string name)
    {
        var webview = XamlTestFiles.Load(file).Descendants()
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == name);

        // The WebView2 child HWND swallows WM_NCHITTEST, so left/right/bottom resize only works
        // where the top-level window owns the pixels: the band must equal the policy's resize DIP.
        var d = BorderlessResizeHitTestPolicy.ResizeBorderDip;
        var margins = webview.Descendants(XamlTestFiles.Pres + "Setter")
            .Where(s => s.Attribute("Property")?.Value == "Margin")
            .Select(s => s.Attribute("Value")?.Value)
            .ToList();
        Assert.Contains($"{d},0,{d},{d}", margins);   // normal state: band on left/right/bottom
        Assert.Contains("0", margins);                // maximized: full-bleed (zones inert there)

        // A direct Margin attribute would silently override the style setters.
        Assert.Null(webview.Attribute("Margin"));

        var trigger = webview.Descendants(XamlTestFiles.Pres + "DataTrigger").Single();
        Assert.Equal("Maximized", trigger.Attribute("Value")?.Value);
        Assert.Contains("WindowState", trigger.Attribute("Binding")?.Value ?? "");
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("PlayerWindow.xaml")]
    public void Caption_buttons_leave_the_top_right_resize_corner_clear(string file)
    {
        var expected = $"0,0,{BorderlessResizeHitTestPolicy.ResizeBorderDip},0";
        Assert.Contains(XamlTestFiles.Load(file).Descendants(), element =>
            element.Name == XamlTestFiles.Pres + "StackPanel"
            && element.Attribute("Margin")?.Value == expected);
    }

    // --- Required named controls (code-behind FindName / generated fields depend on these) ---

    [Theory]
    [MemberData(nameof(RequiredNames))]
    public void Required_named_controls_exist(string file, string[] names)
    {
        var present = XamlTestFiles.Load(file).Descendants()
            .Select(e => e.Attribute(XamlTestFiles.X + "Name")?.Value)
            .Where(n => n is not null)
            .ToHashSet();

        foreach (var name in names)
            Assert.Contains(name, present!);
    }

    public static IEnumerable<object[]> RequiredNames() => new[]
    {
        new object[] { "MainWindow.xaml", new[]
        {
            "Browser", "UrlBox", "ProfilesCombo", "PinToggle", "AutoToggle", "TitleText", "PinnedHint", "MainBarBackdrop", "PopOutButton",
            "SourceToolbar", "SourceNavigationGroup", "SourceProfileGroup", "PopOutButtonIcon", "PopOutButtonText",
            "BackButton", "ReloadButton", "HomeButton", "ProfileActionsButton", "ProfileActionsMenu",
            "SaveProfileMenuItem", "EditProfileMenuItem", "DeleteProfileMenuItem", "ShowPopoutButton",
            "SettingsButton", "MinimizeButton", "MaximizeButton", "CloseButton",
            "SourcePlaceholder", "PlaceholderShowPopoutButton", "PlaceholderBringBackButton", "PlaceholderNoteText", "RuntimeErrorPanel", "RuntimeErrorHeading", "RuntimeErrorText", "RuntimeRetryButton",
            "SettingsUnsavedHint",
        }},
        new object[] { "PlayerWindow.xaml", new[]
        {
            "ChromeStrip", "SettingsButton", "FadeToggle", "PinToggle", "ExpandButton", "CloseButton", "Player",
            "ErrorBar", "ErrorText", "FallbackButton", "ErrorDismissButton",
        }},
        new object[] { "SettingsWindow.xaml", new[]
        {
            "SettingsScroll",
            "PrivacySectionHeader", "AppearanceSectionHeader", "AdvancedSectionHeader",
            "ResetAppStateButton", "ResetDescriptionText",
            "ClearBrowserDataButton", "ClearDescriptionText", "CloseButton",
            "ThemeSharpDarkPreset", "ThemeMinimalPreset", "ThemeSoftGlassPreset",
            "AccentTargetText", "AccentPicker",
            "CornerStyleThemeChip", "CornerStyleSquareChip", "CornerStyleSmallChip",
            "CornerStyleRoundChip",
            "FocusedOverlayToggle",
            "FadeDelayShortPreset", "FadeDelayNormalPreset", "FadeDelayLongPreset",
            "ActiveOpacitySlider", "ActiveOpacityValueText", "IdleOpacitySlider", "IdleOpacityValueText",
            "StripAutoHideToggle",
            "DoneButton",
        }},
    };

    // --- Settings is scrollable and sectioned (overhaul Task 5) ---

    [Fact]
    public void Settings_sections_live_inside_the_scroll_viewer_in_order()
    {
        var doc = XamlTestFiles.Load("SettingsWindow.xaml");
        var scroll = doc.Descendants(XamlTestFiles.Pres + "ScrollViewer")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "SettingsScroll");

        // Everyday appearance and Popout controls precede destructive maintenance actions.
        var headers = scroll.Descendants()
            .Select(e => e.Attribute(XamlTestFiles.X + "Name")?.Value)
            .Where(n => n is not null && n.EndsWith("SectionHeader"))
            .ToArray();
        Assert.Equal(new[]
        {
            "AppearanceSectionHeader", "AdvancedSectionHeader", "PrivacySectionHeader",
        }, headers);

        // The title bar must NOT scroll away (CloseButton stays reachable at any content height).
        Assert.DoesNotContain(scroll.Descendants(),
            e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "CloseButton");
    }

    [Fact]
    public void Settings_done_button_lives_in_a_fixed_footer_outside_the_scroll()
    {
        // The primary "Done" confirm (apply + close, mirroring the title-bar close) must stay reachable
        // at any scroll position, so — like CloseButton — it lives OUTSIDE SettingsScroll, in the fixed
        // footer. A Done that scrolled with the sections would vanish under a tall section list.
        var doc = XamlTestFiles.Load("SettingsWindow.xaml");
        var scroll = doc.Descendants(XamlTestFiles.Pres + "ScrollViewer")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "SettingsScroll");
        Assert.DoesNotContain(scroll.Descendants(),
            e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "DoneButton");

        // It is a Button using the shared primary-accent style (not a one-off look).
        var done = doc.Descendants(XamlTestFiles.Pres + "Button")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "DoneButton");
        Assert.Equal("{StaticResource AccentButton}", done.Attribute("Style")?.Value);
    }

    [Fact]
    public void Settings_theme_hint_matches_the_actual_preview_contract()
    {
        var hint = XamlTestFiles.Load("SettingsWindow.xaml").Descendants()
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "ThemeHintText")
            .Attribute("Text")?.Value;

        Assert.Contains("Preview your changes here", hint);
        Assert.Contains("Done keeps them", hint);
        Assert.Contains("Cancel restores your previous look", hint);
        Assert.DoesNotContain("chips", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accent_color_picker_exposes_required_named_parts()
    {
        var names = XamlTestFiles.Load("Controls/AccentColorPicker.xaml").Descendants()
            .Select(e => e.Attribute(XamlTestFiles.X + "Name")?.Value)
            .Where(n => n is not null)
            .ToHashSet();

        Assert.Contains("HueSatDisc", names);
        Assert.Contains("ValueSlider", names);
        Assert.Contains("RInput", names);
        Assert.Contains("GInput", names);
        Assert.Contains("BInput", names);
        Assert.Contains("HexInput", names);
        Assert.Contains("PresetRow", names);
        Assert.Contains("PreviewSwatch", names);
        Assert.Contains("ReadabilityWarning", names);
        Assert.Contains("UseNearestReadableButton", names);
    }

    [Fact]
    public void Accent_color_picker_hue_wheel_capture_has_release_paths()
    {
        var source = File.ReadAllText(Path.Combine(XamlTestFiles.SrcDir, "Controls", "AccentColorPicker.xaml.cs"));

        Assert.Contains("HueSatDisc.MouseLeftButtonUp += HueSatDisc_MouseLeftButtonUp;", source);
        Assert.Contains("Unloaded += (_, _) => ReleaseHueSatCapture();", source);
        Assert.Contains("ReleaseHueSatCapture();", source);
        Assert.Contains("HueSatDisc.ReleaseMouseCapture();", source);
        Assert.Contains("e.LeftButton != MouseButtonState.Pressed", source);
    }

    [Fact]
    public void Accent_button_text_uses_pixel_aligned_rendering()
    {
        var style = XamlTestFiles.Load("Theme/ControlStyles.xaml").Descendants(XamlTestFiles.Pres + "Style")
            .Single(e => e.Attribute(XamlTestFiles.X + "Key")?.Value == "AccentButton");

        string? SetterValue(string property) => style.Elements(XamlTestFiles.Pres + "Setter")
            .SingleOrDefault(s => s.Attribute("Property")?.Value == property)
            ?.Attribute("Value")?.Value;

        Assert.Equal("Display", SetterValue("TextOptions.TextFormattingMode"));
        Assert.Equal("Fixed", SetterValue("TextOptions.TextHintingMode"));
        Assert.Equal("Grayscale", SetterValue("TextOptions.TextRenderingMode"));
        Assert.Equal("{DynamicResource BorderThicknessDefault}", SetterValue("BorderThickness"));
        Assert.Equal("{DynamicResource AccentPrimary}", SetterValue("Background"));
        Assert.Equal("{DynamicResource OnAccent}", SetterValue("Foreground"));

        var presenter = style.Descendants(XamlTestFiles.Pres + "ContentPresenter").Single();
        Assert.Equal("{TemplateBinding Foreground}", presenter.Attribute("TextElement.Foreground")?.Value);
        Assert.Equal("Display", presenter.Attribute("TextOptions.TextFormattingMode")?.Value);
        Assert.Equal("Fixed", presenter.Attribute("TextOptions.TextHintingMode")?.Value);
        Assert.Equal("Grayscale", presenter.Attribute("TextOptions.TextRenderingMode")?.Value);

        var main = XamlTestFiles.Load("MainWindow.xaml");
        foreach (var name in new[] { "PopOutButtonIcon", "PopOutButtonText" })
        {
            var text = main.Descendants(XamlTestFiles.Pres + "TextBlock")
                .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == name);
            Assert.Equal("Display", text.Attribute("TextOptions.TextFormattingMode")?.Value);
            Assert.Equal("Fixed", text.Attribute("TextOptions.TextHintingMode")?.Value);
            Assert.Equal("Grayscale", text.Attribute("TextOptions.TextRenderingMode")?.Value);
            Assert.Equal("{Binding Foreground, ElementName=PopOutButton}", text.Attribute("Foreground")?.Value);
        }
    }

    [Fact]
    public void Source_placeholder_has_bring_video_back_action()
    {
        var buttons = XamlTestFiles.Load("MainWindow.xaml").Descendants(XamlTestFiles.Pres + "Button").ToList();
        var button = buttons
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "PlaceholderBringBackButton");
        var show = buttons
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "PlaceholderShowPopoutButton");

        Assert.Equal("Bring video back", button.Attribute("Content")?.Value);
        Assert.Equal("{StaticResource AccentButton}", button.Attribute("Style")?.Value);
        Assert.Equal("PlaceholderBringBackButton_Click", button.Attribute("Click")?.Value);
        Assert.Equal("Bring video back", button.Attribute("AutomationProperties.Name")?.Value);
        Assert.Contains("Return playback", button.Attribute("ToolTip")?.Value);

        Assert.Equal("Show Popout", show.Attribute("Content")?.Value);
        Assert.Equal("{StaticResource DarkButton}", show.Attribute("Style")?.Value);
        Assert.Equal("PlaceholderShowPopoutButton_Click", show.Attribute("Click")?.Value);
        Assert.NotEqual(show.Attribute("Click")?.Value, button.Attribute("Click")?.Value);
    }

    /// <summary>
    /// Accent reach (P2, 2026-07-14): the FUNCTIONAL toolbar row carries the app accent so a profile
    /// switch visibly re-tints the app. The CAPTION row must stay neutral — the accent shell-tint wash
    /// already sits behind it, so accenting those glyphs too would put accent on accent-tint. This test
    /// is the guard for that split; it is the whole reason the accent is not simply set on IconButton.
    /// </summary>
    [Fact]
    public void Toolbar_glyphs_carry_the_accent_but_window_controls_stay_neutral_REQ_UI_01()
    {
        var doc = XamlTestFiles.Load("MainWindow.xaml");

        string? StyleOf(string name) => doc.Descendants(XamlTestFiles.Pres + "Button")
            .Single(b => (string?)b.Attribute(XamlTestFiles.X + "Name") == name)
            .Attribute("Style")?.Value;

        foreach (var accented in new[]
                 {
                     "BackButton", "ReloadButton", "HomeButton",
                     "ProfileActionsButton", "ShowPopoutButton",
                 })
        {
            Assert.Equal("{StaticResource AccentIconButton}", StyleOf(accented));
        }

        // Window management stays neutral. Close additionally keeps its own red-hover style.
        foreach (var neutral in new[] { "SettingsButton", "MinimizeButton", "MaximizeButton" })
        {
            Assert.Equal("{StaticResource IconButton}", StyleOf(neutral));
        }
        Assert.Equal("{StaticResource CloseIconButton}", StyleOf("CloseButton"));

        // AccentIconButton must override ONLY the foreground — it inherits IconButton for everything
        // else, so the two rows cannot drift apart in size, radius, or hover behaviour.
        var style = XamlTestFiles.Load("Theme/ControlStyles.xaml")
            .Descendants(XamlTestFiles.Pres + "Style")
            .Single(e => (string?)e.Attribute(XamlTestFiles.X + "Key") == "AccentIconButton");
        Assert.Equal("{StaticResource IconButton}", style.Attribute("BasedOn")?.Value);
        var setter = Assert.Single(style.Elements(XamlTestFiles.Pres + "Setter"));
        Assert.Equal("Foreground", setter.Attribute("Property")?.Value);
        // AccentChromeGlyph, NOT AccentPrimary: the glyph rides the user's accent-intensity dial, so at
        // intensity 0 it returns to ordinary text color instead of being stuck on the accent.
        Assert.Equal("{DynamicResource AccentChromeGlyph}", setter.Attribute("Value")?.Value);
    }

    [Fact]
    public void Profile_selector_uses_one_live_contrast_rail_not_nested_fill_REQ_PROFILE_01()
    {
        var doc = XamlTestFiles.Load("MainWindow.xaml");
        var row = doc.Descendants(XamlTestFiles.Pres + "Grid")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "ProfileIdentityRow");

        var rail = row.Descendants(XamlTestFiles.Pres + "Border")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "ProfileIdentityColorRail");
        Assert.Equal("4", rail.Attribute("Width")?.Value);
        Assert.Equal("{DynamicResource SurfaceHover}", rail.Attribute("Tag")?.Value);
        var multi = rail.Element(XamlTestFiles.Pres + "Border.Background")!
            .Element(XamlTestFiles.Pres + "MultiBinding")!;
        Assert.Equal("{StaticResource ContrastBrushConverter}", multi.Attribute("Converter")?.Value);
        var bindings = multi.Elements(XamlTestFiles.Pres + "Binding").ToList();
        Assert.Equal("AccentColor", bindings[0].Attribute("Path")?.Value);
        Assert.Equal("Tag", bindings[1].Attribute("Path")?.Value);
        Assert.Equal("{RelativeSource Self}", bindings[1].Attribute("RelativeSource")?.Value);
        Assert.Single(row.Descendants(XamlTestFiles.Pres + "Border"));
        Assert.Null(row.Attribute("Background"));

        var label = row.Descendants(XamlTestFiles.Pres + "TextBlock")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "ProfileIdentityName");
        Assert.Equal("{Binding Name}", label.Attribute("Text")?.Value);
        Assert.Equal("{DynamicResource TextPrimary}", label.Attribute("Foreground")?.Value);
    }

    [Fact]
    public void Source_title_bar_carries_global_accent_as_a_gradient_wash_REQ_UI_01()
    {
        var backdrop = XamlTestFiles.Load("MainWindow.xaml").Descendants(XamlTestFiles.Pres + "Border")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "MainBarBackdrop");
        var gradient = backdrop.Element(XamlTestFiles.Pres + "Border.Background")!
            .Element(XamlTestFiles.Pres + "LinearGradientBrush")!;
        var stops = gradient.Elements(XamlTestFiles.Pres + "GradientStop").ToList();

        Assert.Equal("{DynamicResource AccentShellTintColor}", stops[0].Attribute("Color")?.Value);
        Assert.Equal("{DynamicResource SurfaceBaseColor}", stops[1].Attribute("Color")?.Value);
        // docs/Theme_Preset_Differences.md: the wash sweeps to 0.80 so it reads as one
        // gradient into the washed background instead of dying mid-bar.
        Assert.Equal("0.80", stops[1].Attribute("Offset")?.Value);
    }

    [Fact]
    public void Popout_button_height_allows_the_largest_theme_density()
    {
        var doc = XamlTestFiles.Load("MainWindow.xaml");
        var toolbarRow = doc.Descendants(XamlTestFiles.Pres + "RowDefinition")
            .ElementAt(1)
            .Attribute("Height")?.Value;
        Assert.Equal("50", toolbarRow);

        var button = doc.Descendants(XamlTestFiles.Pres + "Button")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "PopOutButton");
        var margin = ParseThickness(button.Attribute("Margin")?.Value ?? "");
        var availableHeight = 50 - margin.Top - margin.Bottom;
        var requiredHeight = ThemeCatalog.Presets.Max(p => p.Density.ControlHeight);

        Assert.True(availableHeight >= requiredHeight,
            $"PopOutButton has {availableHeight} DIP available, but the largest theme control height is {requiredHeight} DIP.");
    }

    private static Thickness ParseThickness(string value)
    {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => double.Parse(p, CultureInfo.InvariantCulture))
            .ToArray();

        return parts.Length switch
        {
            1 => new Thickness(parts[0]),
            2 => new Thickness(parts[0], parts[1], parts[0], parts[1]),
            4 => new Thickness(parts[0], parts[1], parts[2], parts[3]),
            _ => throw new FormatException("Unsupported Thickness literal: " + value),
        };
    }

    [Fact]
    public void Settings_window_uses_the_fixed_height_frame()
    {
        // Frame model reconciled with the parallel main landing (b35c0dd): a fixed launch Height
        // (clamped to the work area in code) instead of SizeToContent, so the dialog cannot grow
        // with future sections; the scroll viewer never scrolls sideways.
        var doc = XamlTestFiles.Load("SettingsWindow.xaml");
        var root = doc.Root!;
        Assert.Null(root.Attribute("SizeToContent"));
        Assert.NotNull(root.Attribute("Height"));
        Assert.NotNull(root.Attribute("MinHeight"));

        var scroll = doc.Descendants(XamlTestFiles.Pres + "ScrollViewer")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "SettingsScroll");
        Assert.Equal("Disabled", scroll.Attribute("HorizontalScrollBarVisibility")?.Value);
    }

    [Fact]
    public void Settings_dialog_has_no_outer_border()
    {
        var root = XamlTestFiles.Load("SettingsWindow.xaml").Root!;
        // The outermost Border (the dialog frame) must not draw a stroke.
        var outerBorder = root.Elements(XamlTestFiles.Pres + "Border")
            .First(b => b.Attribute("BorderThickness") is not null);
        Assert.Equal("0", outerBorder.Attribute("BorderThickness")!.Value);
    }

    [Theory]
    [InlineData("CornerStyleThemeChip", "AppearanceSectionHeader", "AdvancedSectionHeader")] // Appearance owns corners
    [InlineData("FocusedOverlayToggle", "AppearanceSectionHeader", "AdvancedSectionHeader")] // Appearance owns presentation
    [InlineData("FadeDelayShortPreset", "AdvancedSectionHeader", null)]                   // Advanced owns fade delay
    [InlineData("ActiveOpacitySlider", "AdvancedSectionHeader", null)]                    // Advanced owns opacity
    [InlineData("StripAutoHideToggle", "AdvancedSectionHeader", null)]                    // Advanced owns auto-hide
    public void Settings_controls_sit_under_their_section(string control, string after, string? before)
    {
        // Document order stands in for section membership: the sections share one StackPanel, so
        // "after its own header (and before the next)" is the structural invariant.
        var ordered = XamlTestFiles.Load("SettingsWindow.xaml").Descendants()
            .Select(e => e.Attribute(XamlTestFiles.X + "Name")?.Value)
            .Where(n => n is not null)
            .ToList();

        Assert.True(ordered.IndexOf(control) > ordered.IndexOf(after),
            $"{control} must follow {after}.");
        if (before is not null)
        {
            Assert.True(ordered.IndexOf(control) < ordered.IndexOf(before),
                $"{control} must precede {before}.");
        }
    }

    // --- Glyph icon-font fallback (REQ-UI-02: no .notdef boxes) + tooltips (UI-CHK-4) ---

    // AccentIconButton is BasedOn IconButton, so it inherits the icon FontFamily and cannot drop a glyph
    // to a .notdef box (REQ-UI-02) — it only overrides Foreground.
    private static readonly HashSet<string> IconFontStyles = new()
    {
        "{StaticResource IconButton}", "{StaticResource AccentIconButton}",
        "{StaticResource CloseIconButton}", "{StaticResource PinToggle}",
    };

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("PlayerWindow.xaml")]
    [InlineData("SettingsWindow.xaml")]
    public void Glyph_controls_use_the_icon_font(string file)
    {
        var doc = XamlTestFiles.Load(file);

        // Any TextBlock whose Text starts with a PUA glyph must declare the icon font inline.
        foreach (var tb in doc.Descendants(XamlTestFiles.Pres + "TextBlock"))
        {
            var text = tb.Attribute("Text")?.Value;
            if (string.IsNullOrEmpty(text) || text[0] < PuaStart) continue;
            var font = tb.Attribute("FontFamily")?.Value;
            Assert.True(font is not null && font.Contains("Segoe Fluent Icons"),
                $"Glyph TextBlock '{text}' in {file} is missing the icon FontFamily.");
        }

        // Any Button/ToggleButton carrying a glyph Content must use an icon-font style.
        foreach (var btn in doc.Descendants().Where(e =>
                     e.Name == XamlTestFiles.Pres + "Button" || e.Name == XamlTestFiles.Pres + "ToggleButton"))
        {
            var content = btn.Attribute("Content")?.Value;
            if (string.IsNullOrEmpty(content) || content[0] < PuaStart) continue;
            var style = btn.Attribute("Style")?.Value;
            Assert.True(style is not null && IconFontStyles.Contains(style),
                $"Glyph button '{content}' in {file} must use an icon-font style (was '{style}').");
        }
    }

    // --- Accessible names for icon-only / templated controls (REQ-UI-02, overhaul Task 7) ---

    [Theory]
    [MemberData(nameof(RequiredAccessibleNames))]
    public void Icon_controls_have_accessible_names(string file, string[] names)
    {
        var byName = XamlTestFiles.Load(file).Descendants()
            .Where(e => e.Attribute(XamlTestFiles.X + "Name") is not null)
            .GroupBy(e => e.Attribute(XamlTestFiles.X + "Name")!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var name in names)
        {
            Assert.False(string.IsNullOrWhiteSpace(byName[name].Attribute("AutomationProperties.Name")?.Value),
                $"{name} in {file} is missing AutomationProperties.Name (REQ-UI-02).");
        }
    }

    public static IEnumerable<object[]> RequiredAccessibleNames() => new[]
    {
        // MaximizeButton's name is deliberately state-neutral ("Maximize or restore"): its glyph
        // flips in code on StateChanged. PopOutButton's static name is its launch-state action;
        // the Task 6 show-popout state change must update it in the same code path as the label.
        // PlayerWindow's ExpandButton follows the MaximizeButton precedent (state-neutral name,
        // glyph/tooltip flip in code).
        new object[] { "MainWindow.xaml", new[]
        {
            "SettingsButton", "MinimizeButton", "MaximizeButton", "CloseButton",
            "BackButton", "ReloadButton", "HomeButton", "UrlBox", "ProfilesCombo",
            "ProfileActionsButton", "SaveProfileMenuItem", "EditProfileMenuItem", "DeleteProfileMenuItem",
            "PinToggle", "AutoToggle", "ShowPopoutButton", "PopOutButton",
            "PlaceholderShowPopoutButton", "PlaceholderBringBackButton",
        }},
        new object[] { "PlayerWindow.xaml", new[] { "SettingsButton", "FadeToggle", "PinToggle", "ExpandButton", "CloseButton" } },
        new object[] { "SettingsWindow.xaml", new[] { "CloseButton", "DoneButton" } },
    };

    [Fact]
    public void Caption_and_toolbar_controls_have_tooltips()
    {
        var byName = XamlTestFiles.Load("MainWindow.xaml").Descendants()
            .Where(e => e.Attribute(XamlTestFiles.X + "Name") is not null)
            .ToDictionary(e => e.Attribute(XamlTestFiles.X + "Name")!.Value);

        foreach (var name in new[]
        {
            "SettingsButton", "MinimizeButton", "MaximizeButton", "CloseButton", "BackButton",
            "ReloadButton", "HomeButton", "UrlBox", "ProfilesCombo", "ProfileActionsButton",
            "PinToggle", "AutoToggle", "ShowPopoutButton", "PlaceholderShowPopoutButton",
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(byName[name].Attribute("ToolTip")?.Value),
                $"{name} is missing a ToolTip (UI-CHK-4).");
        }
    }

    [Fact]
    public void Popout_settings_affordance_reuses_the_shared_settings_contract()
    {
        var settings = XamlTestFiles.Load("PlayerWindow.xaml").Descendants()
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "SettingsButton");

        Assert.Equal("{StaticResource IconButton}", settings.Attribute("Style")?.Value);
        Assert.Equal("\uE713", settings.Attribute("Content")?.Value);
        Assert.Equal("Settings", settings.Attribute("ToolTip")?.Value);
        Assert.Equal("Settings", settings.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("SettingsButton_Click", settings.Attribute("Click")?.Value);
    }

    [Fact]
    public void Popout_fade_copy_does_not_promise_row_collapse_when_the_override_is_off()
    {
        var fade = XamlTestFiles.Load("PlayerWindow.xaml").Descendants()
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "FadeToggle");

        Assert.Equal("Fade top bar when idle", fade.Attribute("AutomationProperties.Name")?.Value);
        Assert.Contains("Settings controls whether its row also hides", fade.Attribute("ToolTip")?.Value);
    }

    // --- Strip auto-hide layout (spec 7.2, Phase 4 Task 4) ---

    [Fact]
    public void Player_strip_row_is_auto_sized_so_a_collapsed_strip_returns_its_height()
    {
        var doc = XamlTestFiles.Load("PlayerWindow.xaml");

        // The collapse works by Visibility on the strip element; a fixed RowDefinition would keep
        // a dead 44-DIP band above the video. The row must be Auto and the strip carries the height.
        var firstRow = doc.Descendants(XamlTestFiles.Pres + "RowDefinition").First();
        Assert.Equal("Auto", firstRow.Attribute("Height")?.Value);

        var strip = doc.Descendants()
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "ChromeStrip");
        Assert.Equal("44", strip.Attribute("Height")?.Value);

        var handle = doc.Descendants()
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "ChromeDragHandle");
        Assert.Equal("0", handle.Attribute("Grid.Column")?.Value);
        Assert.Equal("Transparent", handle.Attribute("Background")?.Value);
        Assert.Equal("SizeAll", handle.Attribute("Cursor")?.Value);
        Assert.Equal("Drag to move popout", handle.Attribute("ToolTip")?.Value);
    }

    // --- Opacity sliders pin the policy floor (spec 7.3, Phase 4) ---

    [Theory]
    [InlineData("ActiveOpacitySlider")]
    [InlineData("IdleOpacitySlider")]
    public void Opacity_slider_minimum_matches_the_policy_ui_floor(string name)
    {
        var slider = XamlTestFiles.Load("SettingsWindow.xaml").Descendants()
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == name);

        Assert.Equal((WindowOpacityPolicy.UiFloor * 100).ToString(), slider.Attribute("Minimum")?.Value);
        Assert.Equal((WindowOpacityPolicy.Max * 100).ToString(), slider.Attribute("Maximum")?.Value);
    }

    [Fact]
    public void Opacity_settings_explain_source_bar_and_whole_popout_scope()
    {
        var settings = XamlTestFiles.Load("SettingsWindow.xaml");
        Assert.Contains(settings.Descendants(XamlTestFiles.Pres + "TextBlock"),
            e => e.Attribute("Text")?.Value == "Opacity");

        var active = settings.Descendants(XamlTestFiles.Pres + "Slider")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "ActiveOpacitySlider");
        var idle = settings.Descendants(XamlTestFiles.Pres + "Slider")
            .Single(e => e.Attribute(XamlTestFiles.X + "Name")?.Value == "IdleOpacitySlider");

        Assert.Equal("Source top bar and active whole popout opacity", active.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("Idle whole popout opacity", idle.Attribute("AutomationProperties.Name")?.Value);
        Assert.Contains("Source top bar", active.Attribute("ToolTip")?.Value);
        Assert.Contains("whole popout", idle.Attribute("ToolTip")?.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The cards advertise each preset's opacity to the user. Asserting the literal "Quiet · 94%" against
    /// the literal in the XAML compares a copy of the number to a copy of the number and lets the two
    /// drift from the preset they describe: retune Minimal to 0.90, fix the one red catalog test, and
    /// Settings still promises 94% while the preset applies 90%. The expected label is therefore DERIVED
    /// from ThemeCatalog — the only source that decides what the preset actually does.
    /// </summary>
    [Theory]
    [InlineData("sharp-dark", "Crisp")]
    [InlineData("minimal", "Quiet")]
    [InlineData("soft-glass", "Glass")]
    public void Theme_preset_cards_advertise_the_opacity_the_preset_actually_applies(string presetId, string role)
    {
        var preset = ThemeCatalog.PresetFor(presetId);
        var expected = $"{role} · {Math.Round(preset.DefaultActiveWindowOpacity * 100)}%";

        var texts = XamlTestFiles.Load("SettingsWindow.xaml")
            .Descendants(XamlTestFiles.Pres + "TextBlock")
            .Select(e => e.Attribute("Text")?.Value)
            .Where(v => v is not null)
            .ToHashSet();

        Assert.Contains(expected, texts);
    }

    /// <summary>
    /// Same drift, on the swatch: the card paints a hardcoded hex that is supposed to BE the preset's
    /// default accent. Change the preset's accent and the card keeps selling the old color.
    /// </summary>
    [Theory]
    [InlineData("sharp-dark")]
    [InlineData("minimal")]
    [InlineData("soft-glass")]
    public void Theme_preset_swatches_paint_the_preset_default_accent(string presetId)
    {
        var expected = ThemeCatalog.PresetFor(presetId).DefaultAccentColor;

        var backgrounds = XamlTestFiles.Load("SettingsWindow.xaml")
            .Descendants(XamlTestFiles.Pres + "Border")
            .Select(e => e.Attribute("Background")?.Value)
            .Where(v => v is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(expected, backgrounds);
    }

    // --- Compact error bar (spec 10.3 / Q-6, Stage 4) ---

    [Fact]
    public void Player_error_bar_is_collapsed_by_default_with_accessible_actions()
    {
        var byName = XamlTestFiles.Load("PlayerWindow.xaml").Descendants()
            .Where(e => e.Attribute(XamlTestFiles.X + "Name") is not null)
            .ToDictionary(e => e.Attribute(XamlTestFiles.X + "Name")!.Value);

        // The bar must never show before an actual compact error.
        Assert.Equal("Collapsed", byName["ErrorBar"].Attribute("Visibility")?.Value);

        // Both actions need a tooltip (UI-CHK-4) and an automation name (accessibility).
        foreach (var name in new[] { "FallbackButton", "ErrorDismissButton" })
        {
            Assert.False(string.IsNullOrWhiteSpace(byName[name].Attribute("ToolTip")?.Value),
                $"{name} is missing a ToolTip (UI-CHK-4).");
            Assert.False(string.IsNullOrWhiteSpace(
                    byName[name].Attribute(XamlTestFiles.Pres + "AutomationProperties.Name")?.Value ??
                    byName[name].Attribute("AutomationProperties.Name")?.Value),
                $"{name} is missing an AutomationProperties.Name.");
        }

        // User-facing wording stays in the settled vocabulary: "normal page", never internal names.
        var fallbackText = byName["FallbackButton"].Attribute("Content")?.Value ?? "";
        Assert.Contains("normal page", fallbackText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PlayerWindow", fallbackText);
        Assert.DoesNotContain("embed", fallbackText, StringComparison.OrdinalIgnoreCase);
    }

    // --- Resource integrity: every {StaticResource} reference resolves to a defined key ---

    [Fact]
    public void Every_StaticResource_reference_is_defined()
    {
        var files = new[]
        {
            "App.xaml", "MainWindow.xaml", "PlayerWindow.xaml", "SettingsWindow.xaml",
            "Theme/ControlStyles.xaml", "Theme/Colors.xaml",
        };

        var defined = new HashSet<string>();
        var referenced = new HashSet<string>();
        var rx = new Regex(@"\{StaticResource\s+([^}]+)\}", RegexOptions.Compiled);

        foreach (var f in files)
        {
            foreach (var el in XamlTestFiles.Load(f).Descendants())
            {
                if (el.Attribute(XamlTestFiles.X + "Key")?.Value is { } key) defined.Add(key.Trim());
                foreach (var a in el.Attributes())
                    foreach (Match m in rx.Matches(a.Value))
                        referenced.Add(m.Groups[1].Value.Trim());
            }
        }

        var missing = referenced.Where(r => !defined.Contains(r)).OrderBy(x => x).ToArray();
        Assert.True(missing.Length == 0, "Undefined StaticResource keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_DynamicResource_reference_is_defined_in_a_reachable_scope()
    {
        // The theme pass moved palette brushes and Radius* tokens to DynamicResource. A typo'd
        // DynamicResource key fails SILENTLY at runtime (null brush / default CornerRadius), so
        // the markup sweep must cover it like the StaticResource one above — and PER SCOPE: a key
        // defined only inside another window's Window.Resources would pass a pooled union check
        // yet resolve to null everywhere else (adversarial review finding). App-level keys (the
        // App.xaml merged dictionaries) are reachable from every window; window-local keys only
        // from their own file.
        var appFiles = new[] { "App.xaml", "Theme/ControlStyles.xaml", "Theme/Colors.xaml" };
        var windowFiles = new[] { "MainWindow.xaml", "PlayerWindow.xaml", "SettingsWindow.xaml" };
        var rx = new Regex(@"\{DynamicResource\s+([^}]+)\}", RegexOptions.Compiled);

        HashSet<string> KeysOf(string f) => XamlTestFiles.Load(f).Descendants()
            .Select(e => e.Attribute(XamlTestFiles.X + "Key")?.Value?.Trim())
            .Where(k => k is not null)
            .ToHashSet()!;
        HashSet<string> RefsOf(string f) => XamlTestFiles.Load(f).Descendants()
            .SelectMany(e => e.Attributes())
            .SelectMany(a => rx.Matches(a.Value).Select(m => m.Groups[1].Value.Trim()))
            .ToHashSet();

        var appDefined = appFiles.SelectMany(KeysOf).ToHashSet();
        foreach (var f in appFiles.Concat(windowFiles))
        {
            var reachable = appFiles.Contains(f) ? appDefined : appDefined.Union(KeysOf(f)).ToHashSet();
            var missing = RefsOf(f).Where(r => !reachable.Contains(r)).OrderBy(x => x).ToArray();
            Assert.True(missing.Length == 0,
                $"{f}: DynamicResource keys unreachable from its scope: " + string.Join(", ", missing));
        }
    }

    // --- Theme-owned rounding (docs/Theme_Preset_Differences.md): no scattered hardcoded radii ---

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("PlayerWindow.xaml")]
    [InlineData("SettingsWindow.xaml")]
    [InlineData("Theme/ControlStyles.xaml")]
    public void No_hardcoded_corner_radii_outside_the_token_dictionary(string file)
    {
        // Every control radius must ride a semantic Radius* token via DYNAMIC resource (a
        // StaticResource reference yields correct initial values but silently defeats the live
        // theme/corner-style restyle) so themes actually own rounding. The ONLY allowed literal
        // is WindowChrome.CornerRadius="0": WindowChrome is not a FrameworkElement (no dynamic
        // lookup), and the real outer corner belongs to native DWM/window-region shaping — tests pin 0.
        // Setter-form radii are swept too (the adversarial review's escape hatch).
        void AssertRadius(string radius, string context)
        {
            if (radius.StartsWith("{"))
            {
                Assert.True(radius.StartsWith("{DynamicResource Radius"),
                    $"{file}: CornerRadius \"{radius}\" on {context} must be a {{DynamicResource Radius*}} token.");
                return;
            }
            Assert.True(context == "WindowChrome" && radius == "0",
                $"{file}: hardcoded CornerRadius=\"{radius}\" on {context} — use a Radius* token.");
        }

        foreach (var el in XamlTestFiles.Load(file).Descendants())
        {
            if (el.Attribute("CornerRadius")?.Value is { } direct)
                AssertRadius(direct, el.Name.LocalName);
            if (el.Name.LocalName == "Setter" && el.Attribute("Property")?.Value == "CornerRadius" &&
                el.Attribute("Value")?.Value is { } setterValue)
                AssertRadius(setterValue, "Setter");
        }
    }

    [Fact]
    public void Colors_xaml_seeds_match_the_sharp_dark_preset()
    {
        // The Colors.xaml surface/border/text seeds cover design time and the pre-Apply instant;
        // sharp-dark is the default theme, so the seeds must BE its palette and radii or a fresh
        // launch would flash different values before ThemeResourceApplier runs.
        var sharpDark = ThemeCatalog.PresetFor("sharp-dark");
        var tokens = ColorTokens();
        var palette = sharpDark.Palette;
        foreach (var (key, hex) in new[]
        {
            ("AppBackgroundColor", palette.AppBackground), ("SurfaceBaseColor", palette.SurfaceBase),
            ("SurfaceRaisedColor", palette.SurfaceRaised), ("SurfaceHoverColor", palette.SurfaceHover),
            ("BorderSubtleColor", palette.BorderSubtle), ("BorderStrongColor", palette.BorderStrong),
            ("TextPrimaryColor", palette.TextPrimary), ("TextSecondaryColor", palette.TextSecondary),
            ("DangerPinColor", palette.Danger),
        })
        {
            Assert.Equal("#FF" + hex.TrimStart('#'), tokens[key]);
        }

        var radiusTokens = XamlTestFiles.Load("Theme/Colors.xaml")
            .Descendants(XamlTestFiles.Pres + "CornerRadius")
            .ToDictionary(
                e => e.Attribute(XamlTestFiles.X + "Key")!.Value,
                e => e.Value.Trim());
        var radii = sharpDark.Radii;
        // Invariant culture: a future fractional radius must compare as "4.5", never "4,5"
        // (which a comma-decimal locale would produce — and which collides with CornerRadius
        // four-component syntax).
        string Inv(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (key, expected) in new[]
        {
            ("RadiusMainWindowFrame", Inv(radii.MainWindowFrame)),
            ("RadiusPopoutFrame", Inv(radii.PopoutFrame)),
            ("RadiusTitleBar", $"{Inv(radii.TitleBar)},{Inv(radii.TitleBar)},0,0"),
            ("RadiusButton", Inv(radii.Button)),
            ("RadiusIconButton", Inv(radii.IconButton)),
            ("RadiusInput", Inv(radii.Input)),
            ("RadiusPanel", Inv(radii.Panel)),
            ("RadiusPopup", Inv(radii.Popup)),
            ("RadiusThumbnail", Inv(radii.Thumbnail)),
            ("RadiusSwatch", Inv(radii.Swatch)),
            ("RadiusScrollbarThumb", Inv(radii.ScrollbarThumb)),
            ("RadiusToolTip", Inv(radii.ToolTip)),
            // Compatibility aliases follow Input/Button (docs/Theme_Preset_Differences.md).
            ("ControlCornerRadius", Inv(radii.Input)),
            ("ButtonCornerRadius", Inv(radii.Button)),
        })
        {
            Assert.Equal(expected, radiusTokens[key]);
        }
    }

    [Fact]
    public void Colors_xaml_density_and_elevation_seeds_match_the_sharp_dark_preset()
    {
        // The Colors.xaml density/border/elevation seeds cover design time and the pre-Apply instant;
        // sharp-dark is the default theme, so the seeds must BE its ThemeDensity (and its null inner
        // elevation) or a fresh launch would render different control sizes/paddings/shadows before
        // ThemeResourceApplier runs. Pinned to the catalog so the seeds and ThemeDensity cannot drift.
        var doc = XamlTestFiles.Load("Theme/Colors.xaml");
        var density = ThemeCatalog.PresetFor("sharp-dark").Density;
        string Inv(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Hv(Thickness t) => $"{Inv(t.Left)},{Inv(t.Top)}";   // the "horizontal,vertical" seed shorthand

        // Double seeds (heights / icon size / scrollbar thickness): matched by local name because the
        // `sys` (clr-namespace:System) namespace prefix is test-irrelevant.
        var doubles = doc.Descendants()
            .Where(e => e.Name.LocalName == "Double" && e.Attribute(XamlTestFiles.X + "Key") is not null)
            .ToDictionary(e => e.Attribute(XamlTestFiles.X + "Key")!.Value, e => e.Value.Trim());
        Assert.Equal(Inv(density.ControlHeight), doubles["DensityControlHeight"]);
        Assert.Equal(Inv(density.IconButtonSize), doubles["DensityIconButtonSize"]);
        Assert.Equal(Inv(density.ScrollbarThickness), doubles["DensityScrollbarThickness"]);

        // Thickness seeds (paddings + the uniform default border). BorderThicknessDefault MUST be a
        // <Thickness> resource (a double/string would crash the .NET 10 DynamicResource consumer), and
        // its uniform 1 seeds as the single-value "1" shorthand.
        var thicknesses = doc.Descendants(XamlTestFiles.Pres + "Thickness")
            .ToDictionary(e => e.Attribute(XamlTestFiles.X + "Key")!.Value, e => e.Value.Trim());
        Assert.Equal(Hv(density.ButtonPadding), thicknesses["DensityButtonPadding"]);
        Assert.Equal(Hv(density.InputPadding), thicknesses["DensityInputPadding"]);
        Assert.Equal(Hv(density.MenuItemPadding), thicknesses["DensityMenuItemPadding"]);
        Assert.Equal(Hv(density.PresetChipPadding), thicknesses["DensityPresetChipPadding"]);
        Assert.Equal(Hv(density.ToolTipPadding), thicknesses["DensityToolTipPadding"]);
        Assert.Equal(new Thickness(1), density.BorderThicknessDefault);   // catalog stays uniform 1...
        Assert.Equal("1", thicknesses["BorderThicknessDefault"]);          // ...and seeds as the "1" shorthand

        // Sharp Dark has a null inner elevation: both keys exist as x:Null seeds, so the design-time /
        // pre-Apply Effect is null (flat) — exactly the applied value for the default theme.
        var nullKeys = doc.Descendants(XamlTestFiles.X + "Null")
            .Select(e => e.Attribute(XamlTestFiles.X + "Key")?.Value)
            .Where(k => k is not null)
            .ToHashSet();
        Assert.Contains("ElevationPopup", nullKeys);
        Assert.Contains("ElevationPanel", nullKeys);
    }

    [Fact]
    public void Migrated_density_setters_reference_the_density_tokens()
    {
        // FEAS-08: the No_hardcoded_corner_radii sweep is CornerRadius-only; it cannot catch a residual
        // literal left at a migrated Padding/Height/BorderThickness site (the DynamicResource sweep
        // passes vacuously for a site that kept its literal). Positively assert each migration-list
        // top-level Setter references the expected Density*/BorderThicknessDefault DynamicResource key,
        // by style (x:Key or implicit TargetType) and property — so a literal cannot survive silently.
        var controls = XamlTestFiles.Load("Theme/ControlStyles.xaml");
        var settings = XamlTestFiles.Load("SettingsWindow.xaml");

        void AssertSetter(XDocument doc, string styleKeyOrType, string property, string expectedKey)
        {
            var style = doc.Descendants(XamlTestFiles.Pres + "Style").Single(s =>
                s.Attribute(XamlTestFiles.X + "Key")?.Value == styleKeyOrType ||
                (s.Attribute(XamlTestFiles.X + "Key") is null && s.Attribute("TargetType")?.Value == styleKeyOrType));
            // Only the style's OWN (direct-child) Setters — never the template/trigger Setters nested
            // deep inside the Template setter — so e.g. an intentionally-0 borderless override elsewhere
            // can't be mistaken for the migrated base value.
            var setter = style.Elements(XamlTestFiles.Pres + "Setter")
                .Single(s => s.Attribute("Property")?.Value == property);
            Assert.Equal($"{{DynamicResource {expectedKey}}}", setter.Attribute("Value")?.Value);
        }

        AssertSetter(controls, "DarkButton", "Padding", "DensityButtonPadding");
        AssertSetter(controls, "DarkButton", "BorderThickness", "BorderThicknessDefault");
        AssertSetter(controls, "AccentButton", "BorderThickness", "BorderThicknessDefault");
        AssertSetter(controls, "DarkTextBox", "MinHeight", "DensityControlHeight");
        AssertSetter(controls, "DarkTextBox", "Padding", "DensityInputPadding");
        AssertSetter(controls, "DarkTextBox", "BorderThickness", "BorderThicknessDefault");
        AssertSetter(controls, "IconButton", "Width", "DensityIconButtonSize");
        AssertSetter(controls, "IconButton", "Height", "DensityIconButtonSize");
        AssertSetter(controls, "PinToggle", "Width", "DensityIconButtonSize");
        AssertSetter(controls, "PinToggle", "Height", "DensityIconButtonSize");
        AssertSetter(controls, "DarkComboBoxItem", "Padding", "DensityMenuItemPadding");
        AssertSetter(controls, "DarkComboBox", "Height", "DensityControlHeight");
        AssertSetter(controls, "DarkComboBox", "BorderThickness", "BorderThicknessDefault");
        AssertSetter(controls, "ScrollBar", "Width", "DensityScrollbarThickness");
        // MinWidth rides the same token — WPF coerces rendered width to >= MinWidth, so a literal here
        // would silently oversize the Sharp scrollbar; lock it too (review feas08-misses-scrollbar-minwidth).
        AssertSetter(controls, "ScrollBar", "MinWidth", "DensityScrollbarThickness");
        AssertSetter(controls, "ToolTip", "BorderThickness", "BorderThicknessDefault");
        AssertSetter(settings, "PresetToggle", "Height", "DensityControlHeight");
    }

    // --- Theme contrast (WCAG) computed from the actual Colors.xaml tokens ---

    private static Dictionary<string, string> ColorTokens()
    {
        return XamlTestFiles.Load("Theme/Colors.xaml")
            .Descendants(XamlTestFiles.Pres + "Color")
            .Where(e => e.Attribute(XamlTestFiles.X + "Key") is not null)
            .ToDictionary(
                e => e.Attribute(XamlTestFiles.X + "Key")!.Value,
                e => e.Value.Trim());
    }

    [Theory]
    [InlineData("TextPrimaryColor", "SurfaceRaisedColor", 4.5)]   // URL box (UI-CHK-5)
    [InlineData("TextPrimaryColor", "AppBackgroundColor", 4.5)]
    [InlineData("TextPrimaryColor", "SurfaceBaseColor", 4.5)]
    [InlineData("TextSecondaryColor", "SurfaceBaseColor", 4.5)]   // secondary text / empty state
    public void Theme_contrast_meets_minimum(string fg, string bg, double min)
    {
        var t = ColorTokens();
        var ratio = Wcag.ContrastRatio(t[fg], t[bg]);
        Assert.True(ratio >= min, $"{fg} on {bg} = {ratio:F2}:1, below {min}:1.");
    }

    [Fact]
    public void Accent_button_text_reads_on_the_accent_fill()
    {
        var t = ColorTokens();
        var text = Wcag.ContrastRatio(t["OnAccentColor"], t["AccentPrimaryColor"]);
        Assert.True(text >= 4.5, $"Accent button text contrast = {text:F2}:1.");
    }

    [Fact]
    public void Accent_primary_token_defaults_to_the_sharp_dark_cyan()
    {
        // Overhaul Task 9: the theme accent token defaults to the existing cyan as a markup fallback
        // before ThemeResourceApplier runs. AccentCyan stays defined as a compatibility alias; the
        // hover companion AccentPrimaryLight now aliases the v2 AccentHover (Task 4).
        var t = ColorTokens();
        Assert.Equal(t["AccentCyanColor"], t["AccentPrimaryColor"]);
        Assert.Equal(t["AccentHoverColor"], t["AccentPrimaryLightColor"]);
    }

    [Fact]
    public void Colors_xaml_accent_seeds_match_the_default_derived_set_REQ_UI_01()
    {
        // The design-time / pre-Apply accent seeds must BE the derived set for the default accent
        // (cyan) under the default theme (sharp-dark), or a fresh launch flashes wrong accents before
        // ThemeResourceApplier runs. Pinned to ThemeColors.DeriveAccentSet so they cannot drift.
        var set = ThemeColors.DeriveAccentSet(ThemeCatalog.DefaultAccentColor, ThemeCatalog.PresetFor("sharp-dark"));
        var t = ColorTokens();
        static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        Assert.Equal(Hex(set.Primary), t["AccentPrimaryColor"]);
        Assert.Equal(Hex(set.Hover), t["AccentHoverColor"]);
        Assert.Equal(Hex(set.Hover), t["AccentPrimaryLightColor"]);   // alias to AccentHover
        Assert.Equal(Hex(set.Pressed), t["AccentPressedColor"]);
        Assert.Equal(Hex(set.Border), t["AccentBorderColor"]);
        Assert.Equal(Hex(set.ShellTint), t["AccentShellTintColor"]);
        // The toolbar glyph seed rides the DEFAULT accent intensity (the derive above passes none, so it
        // takes DefaultAccentIntensity) — without this the seed is a magic value and a fresh launch can
        // flash the wrong glyph color before ThemeResourceApplier runs.
        Assert.Equal(Hex(set.ChromeGlyph), t["AccentChromeGlyphColor"]);
        Assert.Equal(Hex(set.OnAccent), t["OnAccentColor"]);
        Assert.Equal(Hex(set.OnAccentPressed), t["OnAccentPressedColor"]);
        // Background room tones (docs/Theme_Preset_Differences.md): seeds ARE the derived defaults.
        Assert.Equal(Hex(set.Letterbox), t["AccentLetterboxColor"]);
        Assert.Equal(Hex(set.BackgroundWash), t["AppBackgroundWashColor"]);
        Assert.Equal(Hex(set.PopoutEdge), t["PopoutAccentEdgeColor"]);
    }

    [Theory]
    [InlineData("AccentCyanColor")]
    [InlineData("AccentVioletColor")]
    [InlineData("AccentGreenColor")]
    [InlineData("AccentAmberColor")]
    public void Customization_accents_are_readable_as_active_glyphs_on_hover_surface(string accent)
    {
        var t = ColorTokens();
        var ratio = Wcag.ContrastRatio(t[accent], t["SurfaceHoverColor"]);
        Assert.True(ratio >= 3.0, $"{accent} on SurfaceHoverColor = {ratio:F2}:1, below 3:1.");
    }

    [Fact]
    public void Theme_accent_palette_is_readable()
    {
        // The curated preset chips still need to work in the places they are offered as fixed
        // choices: active glyphs on hover surfaces and dark text on filled primary buttons.
        var hover = ColorTokens()["SurfaceHoverColor"];
        foreach (var option in ThemeCatalog.AccentOptions)
        {
            var glyph = Wcag.ContrastRatio(option.HexColor, hover);
            Assert.True(glyph >= 3.0, $"Accent {option.Key} ({option.HexColor}) on hover surface = {glyph:F2}:1.");

            var text = Wcag.ContrastRatio("#FF06141A", option.HexColor);
            Assert.True(text >= 4.5, $"Dark button text on accent {option.Key} ({option.HexColor}) = {text:F2}:1.");
        }
    }

    [Fact]
    public void Settings_theme_and_accent_controls_match_the_catalog()
    {
        var controls = XamlTestFiles.Load("SettingsWindow.xaml").Descendants()
            .Where(e => e.Attribute(XamlTestFiles.X + "Name") is not null)
            .ToList();

        string? Tag(string name) => controls
            .First(e => e.Attribute(XamlTestFiles.X + "Name")!.Value == name).Attribute("Tag")?.Value;
        IEnumerable<string> NamesWhere(Func<string, bool> pred) => controls
            .Select(e => e.Attribute(XamlTestFiles.X + "Name")!.Value).Where(pred);

        // Preset chip Tags are the catalog preset ids.
        // Order-independent so the hand-written markup and the catalog cannot drift apart.
        var presetTags = NamesWhere(n => n.StartsWith("Theme") && n.EndsWith("Preset")).Select(Tag).ToHashSet();
        Assert.Equal(ThemeCatalog.Presets.Select(p => p.Id).ToHashSet(), presetTags!);

        // Corner-style chip Tags are the catalog corner style keys (docs/Theme_Preset_Differences.md).
        var cornerTags = NamesWhere(n => n.StartsWith("CornerStyle")).Select(Tag).ToHashSet();
        Assert.Equal(ThemeCatalog.CornerStyleOptions.Select(o => o.Key).ToHashSet(), cornerTags!);
    }

    [Fact]
    public void Settings_appearance_controls_have_tooltips_and_accessible_names()
    {
        var byName = XamlTestFiles.Load("SettingsWindow.xaml").Descendants()
            .Where(e => e.Attribute(XamlTestFiles.X + "Name") is not null)
            .GroupBy(e => e.Attribute(XamlTestFiles.X + "Name")!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var name in new[]
        {
            "ThemeSharpDarkPreset", "ThemeMinimalPreset", "ThemeSoftGlassPreset",
            "AccentPicker",
            "CornerStyleThemeChip", "CornerStyleSquareChip", "CornerStyleSmallChip",
            "CornerStyleRoundChip",
            "FocusedOverlayToggle",
            "FadeDelayShortPreset", "FadeDelayNormalPreset", "FadeDelayLongPreset",
            "ActiveOpacitySlider", "IdleOpacitySlider",
            "StripAutoHideToggle",
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(byName[name].Attribute("ToolTip")?.Value),
                $"{name} is missing a ToolTip.");
            Assert.False(string.IsNullOrWhiteSpace(byName[name].Attribute("AutomationProperties.Name")?.Value),
                $"{name} is missing AutomationProperties.Name.");
        }
    }

    [Fact]
    public void SettingsWindow_is_not_transparent()
    {
        // The Settings dialog hosts no WebView2, but stays opaque for visual consistency.
        var w = XamlTestFiles.Load("SettingsWindow.xaml").Root!;
        Assert.NotEqual("True", w.Attribute("AllowsTransparency")?.Value);
    }

    [Fact]
    public void DangerButton_style_is_defined()
    {
        var keys = XamlTestFiles.Load("Theme/ControlStyles.xaml").Descendants()
            .Select(e => e.Attribute(XamlTestFiles.X + "Key")?.Value)
            .Where(k => k is not null);
        Assert.Contains("DangerButton", keys);
    }

    [Fact]
    public void Grey_border_tokens_are_quieted_to_a_faint_hairline()
    {
        var colors = XamlTestFiles.Load("Theme/Colors.xaml");
        string ColorOf(string key) => colors
            .Descendants(XamlTestFiles.Pres + "Color")
            .Single(e => (string?)e.Attribute(XamlTestFiles.X + "Key") == key)
            .Value.Trim();

        // Softened from the old hard greys (#FF2B3645 / #FF3E4B5C) so control outlines read as a
        // faint hairline on the dark UI instead of a boxed-in grey rectangle.
        Assert.Equal("#FF181F29", ColorOf("BorderSubtleColor"));
        Assert.Equal("#FF262F3D", ColorOf("BorderStrongColor"));
    }

    [Fact]
    public void Resting_control_borders_are_transparent_but_focus_ring_survives()
    {
        var styles = XamlTestFiles.Load("Theme/ControlStyles.xaml");

        string RestingBorderBrush(string key)
        {
            var style = styles.Descendants(XamlTestFiles.Pres + "Style")
                .Single(e => (string?)e.Attribute(XamlTestFiles.X + "Key") == key);
            // The style's own (resting) BorderBrush setter — not template-trigger setters.
            return style.Elements(XamlTestFiles.Pres + "Setter")
                .Single(s => (string?)s.Attribute("Property") == "BorderBrush")
                .Attribute("Value")!.Value;
        }

        Assert.Equal("Transparent", RestingBorderBrush("DarkButton"));
        Assert.Equal("Transparent", RestingBorderBrush("AccentButton"));
        Assert.Equal("Transparent", RestingBorderBrush("DarkTextBox"));

        // DarkComboBox remains one neutral dark control; profile identity lives only in the row rail.
        // The toggle border is the inline Border x:Name="bd" inside the ToggleButton template;
        // the dropdown popup border (x:Name="DropDownBorder") intentionally keeps BorderSubtle.
        var comboStyle = styles.Descendants(XamlTestFiles.Pres + "Style")
            .Single(e => (string?)e.Attribute(XamlTestFiles.X + "Key") == "DarkComboBox");
        var comboDescBorders = comboStyle.Descendants(XamlTestFiles.Pres + "Border").ToList();
        var toggleBorder = comboDescBorders
            .Single(b => (string?)b.Attribute(XamlTestFiles.X + "Name") == "bd");
        var dropDownBorder = comboDescBorders
            .Single(b => (string?)b.Attribute(XamlTestFiles.X + "Name") == "DropDownBorder");
        Assert.Equal("Transparent", toggleBorder.Attribute("BorderBrush")?.Value);
        Assert.Equal("{DynamicResource BorderSubtle}", dropDownBorder.Attribute("BorderBrush")?.Value);

        // DarkTextBox keyboard-focus trigger must still paint the accent ring (REQ-UI-02).
        var textBox = styles.Descendants(XamlTestFiles.Pres + "Style")
            .Single(e => (string?)e.Attribute(XamlTestFiles.X + "Key") == "DarkTextBox");
        var focusTrigger = textBox.Descendants(XamlTestFiles.Pres + "Trigger")
            .Single(t => (string?)t.Attribute("Property") == "IsKeyboardFocusWithin");
        Assert.Contains("AccentBorder",
            focusTrigger.Descendants(XamlTestFiles.Pres + "Setter")
                .Single(s => (string?)s.Attribute("Property") == "BorderBrush")
                .Attribute("Value")!.Value);
    }

    // --- App manifest declares per-monitor-v2 DPI awareness (REQ-WINDOW-01, Q-7) ---

    [Fact]
    public void App_manifest_declares_per_monitor_v2_dpi()
    {
        var dpiAwareness = XamlTestFiles.Load("app.manifest")
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "dpiAwareness");

        Assert.NotNull(dpiAwareness);
        Assert.Contains("PerMonitorV2", dpiAwareness!.Value);
    }
}
