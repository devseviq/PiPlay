using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PiPlay.Controls;
using PiPlay.Services;
using PiPlay.Theme;

namespace PiPlay;

/// <summary>The action the user confirmed in the Settings window, read by MainWindow after close.</summary>
internal enum PrivacyAction { None, ResetAppState, ClearBrowserData }

/// <summary>
/// Themed Settings window (spec 12, Phase 2). Hosts the Privacy section. It confirms an action and
/// records <see cref="RequestedAction"/>, then closes — it performs no app/WebView work itself
/// (MainWindow does, after the modal closes). Visible wording is sourced from
/// <see cref="PrivacyService"/> so the UI and the tested constants cannot drift.
/// </summary>
public partial class SettingsWindow : Window
{
    internal PrivacyAction RequestedAction { get; private set; } = PrivacyAction.None;

    /// <summary>True when any persisted player preference changed (theme, accent, fade delay, or
    /// Popout presentation), so MainWindow knows to persist and re-apply on close.</summary>
    internal bool AppearanceChanged { get; private set; }

    /// <summary>The selected theme preset id and the single accent hex the picker drives.</summary>
    internal string ThemeId { get; private set; }
    internal string AccentColor { get; private set; }

    /// <summary>Corner profile override (docs/Theme_Preset_Differences.md): "theme" follows the preset; the other
    /// styles swap the whole radius + native-corner profile.</summary>
    internal string CornerStyle { get; private set; }
    internal int FadeIdleDelayMs { get; private set; }
    internal bool CompactMode { get; private set; }

    /// <summary>Whether new Popout Players use the optional media-first Focused presentation.</summary>
    internal bool FocusedPresentation { get; private set; }

    /// <summary>Nullable behavior OVERRIDES (theme code review P2): null = follow the selected
    /// preset's default. Only touching a behavior control (or a persisted override seeded into
    /// the ctor) makes a value explicit — an accent-only apply keeps nulls null, so the user
    /// keeps following future preset behavior defaults.</summary>
    internal double? ActiveOpacityOverride { get; private set; }

    /// <inheritdoc cref="ActiveOpacityOverride"/>
    internal double? IdleOpacityOverride { get; private set; }

    /// <inheritdoc cref="ActiveOpacityOverride"/>
    internal bool? StripAutoHideOverride { get; private set; }

    /// <summary>The EFFECTIVE levels (override ?? preset default): what the sliders display,
    /// the live preview sends, and the legacy Player mirrors persist.</summary>
    internal double ConstantWindowOpacity => WindowOpacityPolicy.Normalize(
        ActiveOpacityOverride ?? ThemeCatalog.PresetFor(ThemeId).DefaultActiveWindowOpacity);

    /// <inheritdoc cref="ConstantWindowOpacity"/>
    internal double IdleWindowOpacity => WindowOpacityPolicy.Normalize(
        IdleOpacityOverride ?? ThemeCatalog.PresetFor(ThemeId).DefaultIdleWindowOpacity);

    /// <inheritdoc cref="ConstantWindowOpacity"/>
    internal bool StripAutoHide =>
        StripAutoHideOverride ?? ThemeCatalog.PresetFor(ThemeId).DefaultStripAutoHide;

    /// <summary>Raised on every opacity slider move so MainWindow can live-preview the levels on
    /// the open popout (spec 7.3 / docs/Theme_Preset_Differences.md). Args: (constant, idle).</summary>
    internal event Action<double, double>? OpacityPreviewChanged;
    internal event Action<string>? AccentPreviewChanged;

    /// <summary>Raised after a preset/corner selection so MainWindow can preview the complete shared
    /// resource set while retaining ownership of the transaction and cancel/revert path.</summary>
    internal event Action? ThemePreviewChanged;

    /// <summary>Raised on every accent-intensity slider move so MainWindow can live-preview how far the
    /// accent reaches into the chrome. Arg: the 0–100 intensity.</summary>
    internal event Action<int>? AccentIntensityPreviewChanged;

    /// <summary>How far the accent reaches into the chrome (0–100). Committed by Done.</summary>
    internal int AccentIntensity { get; private set; } = ThemeCatalog.DefaultAccentIntensity;

    // True from construction until the ctor has seeded the sliders: Slider coerces Value to its
    // Minimum during InitializeComponent, which fires ValueChanged before our values are in.
    private bool _seedingOpacitySliders = true;
    private bool _seedingAccentPicker = true;
    private bool _seedingAccentIntensity = true;
    private readonly bool _accentFollowsThemePreset;

    public SettingsWindow(
        bool isBrowserReady,
        string? clearBrowserDataUnavailableHint = null,
        string? themeId = ThemeCatalog.DefaultThemeId,
        string? accentColor = ThemeCatalog.DefaultAccentColor,
        int fadeIdleDelayMs = PlayerAppearancePolicy.DefaultFadeIdleDelayMs,
        bool compactMode = false,
        double? activeOpacityOverride = null,
        double? idleOpacityOverride = null,
        bool? stripAutoHideOverride = null,
        string? cornerStyle = ThemeCatalog.DefaultCornerStyle,
        string? accentEditContext = null,
        int? accentIntensity = null,
        bool accentFollowsThemePreset = true,
        bool focusedPresentation = false)
    {
        _accentFollowsThemePreset = accentFollowsThemePreset;
        InitializeComponent();
        ApplyInitialBounds();
        // The dialog wears the theme/override corner shape itself, and re-applies it on chip
        // clicks — instant feedback for the corner-style row (docs/Theme_Preset_Differences.md).
        SourceInitialized += (_, _) => ApplyOwnCornerMode();

        ThemeId = ThemeCatalog.NormalizeThemeId(themeId);
        AccentColor = ThemeCatalog.NormalizeAccentColor(accentColor);
        AccentPicker.SelectedColor = AccentColor;
        AccentPicker.PreviewColorChanged += AccentPicker_PreviewColorChanged;
        AccentPicker.ReadabilityChanged += ApplyDoneReadability;
        ApplyDoneReadability(AccentPicker.IsSelectedReadable);
        _seedingAccentPicker = false;
        AccentTargetText.Text = string.IsNullOrWhiteSpace(accentEditContext)
            ? "Editing the app accent."
            : accentEditContext;

        AccentIntensity = ThemeCatalog.NormalizeAccentIntensity(accentIntensity);
        AccentIntensitySlider.Value = AccentIntensity;
        _seedingAccentIntensity = false;
        UpdateAccentIntensityValueText();

        CornerStyle = ThemeCatalog.NormalizeCornerStyle(cornerStyle);
        FadeIdleDelayMs = PlayerAppearancePolicy.NormalizeFadeIdleDelayMs(fadeIdleDelayMs);
        CompactMode = compactMode;
        FocusedPresentation = focusedPresentation;
        FocusedOverlayToggle.IsChecked = FocusedPresentation;
        StripAutoHideOverride = stripAutoHideOverride;
        StripAutoHideToggle.IsChecked = StripAutoHide;

        // A hand-edited sub-floor override (the spec 7.3 explicit unlock) is preserved exactly
        // until the user moves that slider: the DISPLAY clamps to the 45% floor, the stored
        // override doesn't.
        ActiveOpacityOverride = NormalizeOverride(activeOpacityOverride);
        IdleOpacityOverride = NormalizeOverride(idleOpacityOverride);
        ActiveOpacitySlider.Value = DisplayPercent(ConstantWindowOpacity);
        IdleOpacitySlider.Value = DisplayPercent(IdleWindowOpacity);
        _seedingOpacitySliders = false;
        UpdateOpacityValueTexts();
        ApplyAppearanceSelections();

        ResetDescriptionText.Text = PrivacyService.ResetDescription;
        ResetAppStateButton.Content = PrivacyService.ResetActionLabel;
        ClearDescriptionText.Text = PrivacyService.ClearDescription;
        ClearBrowserDataButton.Content = PrivacyService.ClearActionLabel;

        SetClearBrowserDataAvailability(isBrowserReady, clearBrowserDataUnavailableHint);
    }

    /// <summary>
    /// Refresh the destructive action while this modal dialog remains open. A timed-out clear may
    /// finish in the background; terminal success or failure makes retry available without forcing
    /// the user to close and reopen Settings.
    /// </summary>
    internal void SetClearBrowserDataAvailability(
        bool isBrowserReady,
        string? clearBrowserDataUnavailableHint = null)
    {
        // Only the Clear action needs a live browser; Reset never does. When it is disabled,
        // explain why (and let the tooltip show on the disabled control).
        var canClearBrowserData = isBrowserReady && string.IsNullOrEmpty(clearBrowserDataUnavailableHint);
        ClearBrowserDataButton.IsEnabled = canClearBrowserData;
        if (canClearBrowserData)
        {
            ClearBrowserDataButton.ToolTip = null;
        }
        else
        {
            ClearBrowserDataButton.ToolTip = clearBrowserDataUnavailableHint
                ?? PrivacyService.ClearNotReadyHint;
            ToolTipService.SetShowOnDisabled(ClearBrowserDataButton, true);
        }
    }

    /// <summary>
    /// Bounded height (overhaul Task 5; frame model reconciled with the b35c0dd landing): the
    /// fixed launch Height clamps to the primary work area less a margin, so the border never
    /// touches the taskbar and shorter displays get the scroll instead of clipping. The floor
    /// keeps the dialog usable if a misreported work area ever comes back tiny.
    /// </summary>
    private void ApplyInitialBounds()
    {
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 48);
        Height = Math.Min(Height, MaxHeight);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DismissWithoutApplying();

    // The footer "Done" is the visible affirmative commit path. Title-bar close/Esc dismisses so
    // MainWindow can revert any live accent preview.
    private void DoneButton_Click(object sender, RoutedEventArgs e) => CompleteDialog();

    internal const string DoneReadyTip = "Apply your changes and close Settings";
    internal const string DoneBlockedTip =
        "Done needs a valid accent such as #2BAED0. Choose a colour or use the default.";

    /// <summary>Done is bound to the picker's readability; the tooltip explains the disabled state (F-6).</summary>
    private void ApplyDoneReadability(bool readable) =>
        DoneButton.ToolTip = readable ? DoneReadyTip : DoneBlockedTip;

    private void DismissWithoutApplying()
    {
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DismissWithoutApplying();
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void ThemePreset_Click(object sender, RoutedEventArgs e)
    {
        var previousPreset = ThemeCatalog.PresetFor(ThemeId);
        ThemeId = ThemeCatalog.NormalizeThemeId(((FrameworkElement)sender).Tag as string);
        // An explicit preset selection adopts the preset's defaults (docs/Theme_Preset_Differences.md) — fade
        // delay, top-bar auto-hide, opacity levels, and theme-owned corners. The controls below
        // can then fine-tune each one; manual changes after this click are overrides. A GLOBAL accent
        // follows the docs/Theme_Preset_Differences.md switch rule (custom values survive). A profile-owned accent is always
        // explicit, even if its bytes equal the old preset default, so a preset switch never rewrites it.
        var preset = ThemeCatalog.PresetFor(ThemeId);
        if (_accentFollowsThemePreset)
            AccentColor = ThemeCatalog.AccentForThemeSwitch(AccentColor, previousPreset, preset);
        CornerStyle = ThemeCatalog.DefaultCornerStyle;
        FadeIdleDelayMs = ThemeCatalog.FadeDelayMillisecondsForPreset(preset.DefaultFadeDelayPreset);
        // Behavior returns to "follow the preset" (code review P2): the overrides reset to null
        // and the controls DISPLAY the new preset's defaults. The seeding guard wraps the slider
        // moves so the programmatic update cannot re-create overrides; the explicit preview
        // invoke still shows the preset's translucency before close.
        StripAutoHideOverride = null;
        ActiveOpacityOverride = null;
        IdleOpacityOverride = null;
        AccentPicker.SelectedColor = AccentColor;
        StripAutoHideToggle.IsChecked = StripAutoHide;
        _seedingOpacitySliders = true;
        ActiveOpacitySlider.Value = DisplayPercent(ConstantWindowOpacity);
        IdleOpacitySlider.Value = DisplayPercent(IdleWindowOpacity);
        _seedingOpacitySliders = false;
        UpdateOpacityValueTexts();
        OpacityPreviewChanged?.Invoke(ConstantWindowOpacity, IdleWindowOpacity);
        AppearanceChanged = true;
        ApplyAppearanceSelections();
        ApplyOwnCornerMode();
        ThemePreviewChanged?.Invoke();
    }

    private void AccentPicker_PreviewColorChanged(string hex)
    {
        if (_seedingAccentPicker) return;
        AccentColor = ThemeCatalog.NormalizeAccentColor(hex);
        AppearanceChanged = true;
        AccentPreviewChanged?.Invoke(AccentColor);
    }

    private void CornerStyle_Click(object sender, RoutedEventArgs e)
    {
        CornerStyle = ThemeCatalog.NormalizeCornerStyle(((FrameworkElement)sender).Tag as string);
        AppearanceChanged = true;
        ApplyAppearanceSelections();
        ApplyOwnCornerMode();
        ThemePreviewChanged?.Invoke();
    }

    private void FocusedOverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        FocusedPresentation = FocusedOverlayToggle.IsChecked == true;
        AppearanceChanged = true;
    }

    /// <summary>The dialog's own native corner shape follows the pending theme/override selection.</summary>
    private void ApplyOwnCornerMode()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;   // SourceInitialized applies the initial state
        WindowOpacityApplier.SetCornerMode(hwnd,
            ThemeCatalog.DwmCornersFor(ThemeCatalog.PresetFor(ThemeId), CornerStyle));
        WindowOpacityApplier.SetBorderColor(hwnd, suppress: true);   // P1 borderless: no Win11 DWM frame hairline
    }

    private void FadeDelay_Click(object sender, RoutedEventArgs e)
    {
        var tag = ((FrameworkElement)sender).Tag as string;
        if (int.TryParse(tag, out var delay))
        {
            FadeIdleDelayMs = PlayerAppearancePolicy.NormalizeFadeIdleDelayMs(delay);
            AppearanceChanged = true;
        }
        ApplyAppearanceSelections();
    }

    private void StripAutoHideToggle_Click(object sender, RoutedEventArgs e)
    {
        StripAutoHideOverride = StripAutoHideToggle.IsChecked == true;
        AppearanceChanged = true;
    }

    private static double DisplayPercent(double level) =>
        Math.Round(Math.Max(level, WindowOpacityPolicy.UiFloor) * 100.0);

    private static double? NormalizeOverride(double? value) =>
        WindowOpacityPolicy.NormalizeOptional(value);   // invalid input = no override, follow the preset

    private void AccentIntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seedingAccentIntensity) return;
        AccentIntensity = ThemeCatalog.NormalizeAccentIntensity((int)Math.Round(AccentIntensitySlider.Value));
        AppearanceChanged = true;
        UpdateAccentIntensityValueText();
        AccentIntensityPreviewChanged?.Invoke(AccentIntensity);
    }

    private void UpdateAccentIntensityValueText() =>
        AccentIntensityValueText.Text = AccentIntensity == 0
            ? "Off"
            : $"{AccentIntensity}%";

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seedingOpacitySliders) return;
        var slider = (Slider)sender;
        var level = Math.Round(slider.Value) / 100.0;
        if (ReferenceEquals(slider, ActiveOpacitySlider)) ActiveOpacityOverride = level;
        else IdleOpacityOverride = level;
        AppearanceChanged = true;
        UpdateOpacityValueTexts();
        OpacityPreviewChanged?.Invoke(ConstantWindowOpacity, IdleWindowOpacity);
    }

    private void UpdateOpacityValueTexts()
    {
        ActiveOpacityValueText.Text = $"{Math.Round(ActiveOpacitySlider.Value)}%";
        IdleOpacityValueText.Text = $"{Math.Round(IdleOpacitySlider.Value)}%";
    }

    private void ResetAppStateButton_Click(object sender, RoutedEventArgs e)
    {
        ResetAppStateButton.IsEnabled = false;
        if (Prompt.AskConfirm(this, PrivacyService.ResetConfirmTitle, PrivacyService.ResetConfirmBody,
                PrivacyService.ResetConfirmButton, danger: PrivacyService.ResetConfirmDanger))
        {
            RequestedAction = PrivacyAction.ResetAppState;
            CompleteDialog();
        }
        else
        {
            ResetAppStateButton.IsEnabled = true;
        }
    }

    private void ClearBrowserDataButton_Click(object sender, RoutedEventArgs e)
    {
        ClearBrowserDataButton.IsEnabled = false;
        if (Prompt.AskConfirm(this, PrivacyService.ClearConfirmTitle, PrivacyService.ClearConfirmBody,
                PrivacyService.ClearConfirmButton, danger: true))
        {
            RequestedAction = PrivacyAction.ClearBrowserData;
            CompleteDialog();
        }
        else
        {
            ClearBrowserDataButton.IsEnabled = true;
        }
    }

    private void ApplyAppearanceSelections()
    {
        SelectByTag(ThemeId, ThemeSharpDarkPreset, ThemeMinimalPreset, ThemeSoftGlassPreset);
        SelectByTag(CornerStyle, CornerStyleThemeChip, CornerStyleSquareChip, CornerStyleSmallChip,
            CornerStyleRoundChip);
        SelectDelay(FadeIdleDelayMs, FadeDelayShortPreset, FadeDelayNormalPreset, FadeDelayLongPreset);
    }

    private static void SelectByTag(string selected, params ToggleButton[] buttons)
    {
        foreach (var button in buttons)
        {
            button.IsChecked = string.Equals(button.Tag as string, selected, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void SelectDelay(int selected, params ToggleButton[] buttons)
    {
        foreach (var button in buttons)
        {
            var matches = int.TryParse(button.Tag as string, out var delay) && delay == selected;
            button.IsChecked = matches;
        }
    }

    private void CompleteDialog()
    {
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }
}
