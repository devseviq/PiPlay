using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PiPlay.Controls;
using PiPlay.Services;
using PiPlay.Theme;

namespace PiPlay;

/// <summary>
/// Minimal themed, fully borderless-dark dialogs (text input, confirm, info). Built in code so they
/// match the app's dark identity exactly — no light native title bar (mirrors SettingsWindow).
/// Self-contained, no extra deps.
/// </summary>
internal static class Prompt
{
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static Style Style(string key) => (Style)Application.Current.Resources[key];

    /// <summary>
    /// Build a borderless dark dialog shell (matches SettingsWindow): no inner border, a thin
    /// draggable title bar with a close button, and a content body the caller fills. Internal so a
    /// WPF test can assert the dark/borderless invariants without showing a modal.
    /// </summary>
    internal static Window BuildShell(Window? owner, string title, out StackPanel body)
    {
        var win = new Window
        {
            Title = title,
            Owner = owner,
            Topmost = owner?.Topmost ?? false,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            ShowInTaskbar = false,
            Background = Brush("AppBackground"),
            UseLayoutRounding = false,
            SnapsToDevicePixels = true,
        };

        // Same native corner shape as the themed owner windows (docs/Theme_Preset_Differences.md): without this,
        // a round/square theme would leave the prompt as the only differently-shaped dialog. And the
        // same P1 borderless treatment — suppress the Win11 DWM frame hairline so prompts match.
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            Services.WindowOpacityApplier.SetCornerMode(hwnd, Theme.ThemeResourceApplier.CurrentDwmCorners);
            Services.WindowOpacityApplier.SetBorderColor(hwnd, suppress: true);
        };

        var root = new DockPanel();

        var bar = new Grid { Height = 42, Background = Brush("SurfaceBase") };
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
        bar.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimary"),
        });
        var close = new Button
        {
            Style = Style("CloseIconButton"),
            Content = "",
            ToolTip = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 5, 0),
        };
        // Icon-only and code-built, so UIA would otherwise expose the bare glyph (REQ-UI-02).
        System.Windows.Automation.AutomationProperties.SetName(close, "Close dialog");
        // Title-bar close behaves as Cancel: set DialogResult=false (which auto-closes the modal)
        // so ShowDialog() returns false, matching the IsCancel button. (Was win.Close() -> null.)
        close.Click += (_, _) => { win.DialogResult = false; };
        bar.Children.Add(close);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        body = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(body);

        win.Content = new Border
        {
            // P1 borderless: no inner frame — the DWM frame is suppressed and the dialog reads as a
            // clean surface (the SurfaceBase title bar + AppBackground body already separate it).
            BorderThickness = new Thickness(0),
            Child = root,
        };
        return win;
    }

    /// <summary>
    /// Build the profile playback-mode picker (spec 10, Phase 3 / spec 17 edit path): a dark combo
    /// offering "Use global default", "Normal page", and — only while the Compact player is enabled
    /// (<see cref="PlaybackModePolicy.CompactPlayerEnabled"/>) — "Compact player". Internal and returns
    /// a getter so a WPF test can round-trip the selection without showing the modal editor. The getter
    /// yields the durable <see cref="Models.Profile.Mode"/> token (null / "normal" / "compact"); an
    /// unknown incoming value is normalized to "use global". While Compact is dormant the dead option is
    /// hidden (so the picker never offers a mode the popout launcher would ignore) and a stored
    /// "compact"/"embed" profile falls back to "Use global default".
    /// </summary>
    internal static (FrameworkElement Element, Func<string?> SelectedMode) BuildModePicker(string? current)
    {
        var normalized = PlaybackModePolicy.NormalizeProfileMode(current);

        ComboBoxItem Item(string text, string? mode) => new() { Content = text, Tag = mode };
        var useGlobal = Item("Use global default", null);
        var normal = Item("Normal page", PlaybackModePolicy.ProfileModeNormal);

        var combo = new ComboBox { Style = Style("DarkComboBox"), Margin = new Thickness(0, 0, 0, 12) };
        combo.Items.Add(useGlobal);
        combo.Items.Add(normal);

        // Compact is dormant (PlaybackModePolicy.CompactPlayerEnabled): hide the dead option. Built via
        // a conditional expression rather than `if (const)` so the flag flips cleanly when it is ever
        // re-enabled, without an unreachable-code warning while it is false.
        ComboBoxItem? compact = PlaybackModePolicy.CompactPlayerEnabled
            ? Item("Compact player", PlaybackModePolicy.ProfileModeCompact)
            : null;
        if (compact is not null) combo.Items.Add(compact);

        combo.SelectedItem = normalized switch
        {
            PlaybackModePolicy.ProfileModeNormal => normal,
            PlaybackModePolicy.ProfileModeCompact when compact is not null => compact,
            _ => useGlobal,
        };

        return (combo, () => (combo.SelectedItem as ComboBoxItem)?.Tag as string);
    }

    /// <summary>
    /// Build the per-profile Popout presentation picker. It is deliberately separate from
    /// <see cref="BuildModePicker"/> because Focused is a watch-page presentation, not Compact
    /// playback. The getter returns the durable profile token (null / "standard" / "focused").
    /// </summary>
    internal static (FrameworkElement Element, Func<string?> SelectedPresentation)
        BuildPresentationPicker(string? current)
    {
        var normalized = PopoutPresentationPolicy.NormalizeProfilePresentation(current);

        ComboBoxItem Item(string text, string? presentation) => new() { Content = text, Tag = presentation };
        var useGlobal = Item("Use global default", null);
        var standard = Item("Standard", PopoutPresentationPolicy.ProfilePresentationStandard);
        var focused = Item("Focused overlay", PopoutPresentationPolicy.ProfilePresentationFocused);

        var combo = new ComboBox { Style = Style("DarkComboBox"), Margin = new Thickness(0, 0, 0, 12) };
        combo.Items.Add(useGlobal);
        combo.Items.Add(standard);
        combo.Items.Add(focused);
        combo.SelectedItem = normalized switch
        {
            PopoutPresentationPolicy.ProfilePresentationStandard => standard,
            PopoutPresentationPolicy.ProfilePresentationFocused => focused,
            _ => useGlobal,
        };

        return (combo, () => (combo.SelectedItem as ComboBoxItem)?.Tag as string);
    }

    /// <summary>Themed text-input dialog (used for naming a profile). Returns null if cancelled.</summary>
    public static string? AskText(Window owner, string title, string message, string initial = "")
    {
        var parts = BuildAskText(owner, title, message, initial);
        return parts.Window.ShowDialog() == true ? parts.Input.Text : null;
    }

    /// <summary>The text prompt's parts, for a WPF test that must not show the modal (polish review 2026-09-10 F-2).</summary>
    internal sealed record TextPromptParts(Window Window, TextBox Input, Button Ok);

    internal static TextPromptParts BuildAskText(Window? owner, string title, string message, string initial)
    {
        var win = BuildShell(owner, title, out var body);
        var caption = new TextBlock
        {
            Text = message,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        body.Children.Add(caption);

        var box = new TextBox
        {
            Text = initial,
            Style = Style("DarkTextBox"),
            Margin = new Thickness(0, 0, 0, 16),
        };
        Label(box, caption);
        body.Children.Add(box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Save", Style = Style("AccentButton"), MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = Style("DarkButton"), MinWidth = 90, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        ok.Click += (_, _) => { win.DialogResult = true; };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return new TextPromptParts(win, box, ok);
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Foreground = Brush("TextSecondary"),
        Margin = new Thickness(0, 0, 0, 4),
    };

    /// <summary>
    /// UIA: a code-built input has no accessible name of its own, so a screen reader announced a
    /// bare "edit" (polish review 2026-09-10 F-2). Its visible caption is the label.
    /// </summary>
    private static void Label(FrameworkElement input, TextBlock caption)
    {
        System.Windows.Automation.AutomationProperties.SetLabeledBy(input, caption);
        System.Windows.Automation.AutomationProperties.SetName(input, caption.Text);
    }

    /// <summary>
    /// Themed Name + URL profile editor (spec 17, Phase 2 edit path). Both fields are prefilled.
    /// The URL is validated inline ("proactive validation UI"): an invalid URL or empty name shows
    /// a themed error and keeps the dialog open instead of closing — so a broken edit fails
    /// gracefully and is never saved. Returns the trimmed <c>(Name, Url)</c>, or null on cancel.
    /// Name-collision policy is the caller's (ProfileService.Update + the overwrite prompt); this
    /// dialog only validates the URL format, so it stays settings-agnostic. The playback-mode
    /// override (spec 10, Phase 3) is carried through as the durable token (null / "normal" /
    /// "compact"); normalization/precedence live in <see cref="PlaybackModePolicy"/>. The Popout
    /// presentation override is a separate durable token (null / "standard" / "focused") owned by
    /// <see cref="PopoutPresentationPolicy"/>.
    /// </summary>
    public static (string Name, string Url, string? Mode, string? Presentation, string? AccentColor)? EditProfile(
        Window owner, string name, string url, string? mode, string? accentColor = null,
        string? fallbackAccentColor = null, Action<string>? accentPreview = null,
        string? presentation = null)
    {
        var parts = BuildEditProfile(owner, name, url, mode, accentColor, fallbackAccentColor, accentPreview, presentation);
        return parts.Window.ShowDialog() == true ? parts.Result() : null;
    }

    /// <summary>The profile editor's parts, for a WPF test that must not show the modal (polish review 2026-09-10 F-2, A-3).</summary>
    internal sealed record EditProfileParts(
        Window Window, TextBox NameBox, TextBox UrlBox, TextBlock ModeCaption, FrameworkElement ModePicker,
        FrameworkElement PresentationPicker, Button Ok,
        Func<(string Name, string Url, string? Mode, string? Presentation, string? AccentColor)?> Result);

    internal static EditProfileParts BuildEditProfile(
        Window? owner, string name, string url, string? mode, string? accentColor = null,
        string? fallbackAccentColor = null, Action<string>? accentPreview = null,
        string? presentation = null)
    {
        var win = BuildShell(owner, "Edit profile", out var body);

        var nameCaption = Caption("Name");
        body.Children.Add(nameCaption);
        var nameBox = new TextBox { Text = name, Style = Style("DarkTextBox"), Margin = new Thickness(0, 0, 0, 12) };
        Label(nameBox, nameCaption);
        body.Children.Add(nameBox);

        var urlCaption = Caption("URL");
        body.Children.Add(urlCaption);
        var urlBox = new TextBox { Text = url, Style = Style("DarkTextBox"), Margin = new Thickness(0, 0, 0, 12) };
        Label(urlBox, urlCaption);
        body.Children.Add(urlBox);

        // Playback mode is hidden while Compact is dormant (polish review 2026-09-10 A-3, spec 10.2):
        // two labels with one outcome. Saving preserves the incoming token while this row is hidden.
        var modeVisibility = PlaybackModePolicy.CompactPlayerEnabled ? Visibility.Visible : Visibility.Collapsed;
        var modeCaption = Caption("Playback mode");
        modeCaption.Visibility = modeVisibility;
        body.Children.Add(modeCaption);
        var (modePicker, selectedMode) = BuildModePicker(mode);
        modePicker.Visibility = modeVisibility;
        Label(modePicker, modeCaption);
        body.Children.Add(modePicker);

        var presentationCaption = Caption("Popout presentation");
        body.Children.Add(presentationCaption);
        var (presentationPicker, selectedPresentation) = BuildPresentationPicker(presentation);
        Label(presentationPicker, presentationCaption);
        body.Children.Add(presentationPicker);

        var useAccent = new CheckBox
        {
            Content = "Profile color",
            IsChecked = accentColor is not null,
            Foreground = Brush("TextPrimary"),
            Margin = new Thickness(0, 12, 0, 8),
            ToolTip = "Use a custom identity color for this profile",
        };
        body.Children.Add(useAccent);

        var accentPicker = new AccentColorPicker
        {
            SelectedColor = accentColor ?? fallbackAccentColor ?? ThemeCatalog.DefaultAccentColor,
            IsEnabled = useAccent.IsChecked == true,
            Margin = new Thickness(0, 0, 0, 12),
        };
        body.Children.Add(accentPicker);

        var error = new TextBlock
        {
            Foreground = Brush("DangerPin"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 12),
        };
        body.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Save", Style = Style("AccentButton"), MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = Style("DarkButton"), MinWidth = 90, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        void UpdateAccentSaveState()
        {
            ok.IsEnabled = CanSaveProfileAccent(useAccent.IsChecked == true, accentPicker);
        }

        accentPicker.PreviewColorChanged += hex =>
        {
            if (useAccent.IsChecked == true)
                accentPreview?.Invoke(hex);
            UpdateAccentSaveState();
        };
        accentPicker.ReadabilityChanged += _ => UpdateAccentSaveState();
        useAccent.Checked += (_, _) =>
        {
            accentPicker.IsEnabled = true;
            if (accentPicker.IsSelectedReadable)
                accentPreview?.Invoke(accentPicker.SelectedColor);
            UpdateAccentSaveState();
        };
        useAccent.Unchecked += (_, _) =>
        {
            accentPicker.IsEnabled = false;
            accentPreview?.Invoke(fallbackAccentColor ?? ThemeCatalog.DefaultAccentColor);
            UpdateAccentSaveState();
        };
        UpdateAccentSaveState();

        (string Name, string Url, string? Mode, string? Presentation, string? AccentColor)? result = null;
        ok.Click += (_, _) =>
        {
            var trimmedName = nameBox.Text.Trim();
            if (trimmedName.Length == 0)
            {
                error.Text = "Enter a name.";
                error.Visibility = Visibility.Visible;
                return;   // keep the dialog open
            }

            var (valid, urlError) = ProfileService.ValidateUrl(urlBox.Text);
            if (!valid)
            {
                error.Text = urlError!;
                error.Visibility = Visibility.Visible;
                return;   // keep the dialog open; nothing is saved
            }

            var editedAccent = useAccent.IsChecked == true ? accentPicker.SelectedColor : null;
            if (!CanSaveProfileAccent(useAccent.IsChecked == true, accentPicker))
            {
                error.Text = "Choose a valid profile color or turn the profile accent off.";
                error.Visibility = Visibility.Visible;
                return;
            }

            var editedMode = modeVisibility == Visibility.Collapsed ? mode : selectedMode();
            result = (trimmedName, urlBox.Text.Trim(), editedMode, selectedPresentation(), editedAccent);
            win.DialogResult = true;
        };
        nameBox.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };

        return new EditProfileParts(win, nameBox, urlBox, modeCaption, modePicker, presentationPicker, ok, () => result);
    }

    internal static bool CanSaveProfileAccent(bool useProfileAccent, AccentColorPicker accentPicker) =>
        !useProfileAccent || (accentPicker.IsSelectedReadable && ProfileService.ValidateAccent(accentPicker.SelectedColor));

    /// <summary>
    /// Themed dark Yes/No confirmation. Returns true only if the user confirms. Default focus is
    /// Cancel so Enter never confirms a destructive action by accident; <paramref name="danger"/>
    /// styles the confirm button as destructive (red). The title-bar close acts as Cancel.
    /// </summary>
    public static bool AskConfirm(Window owner, string title, string message, string confirmText, bool danger = false)
    {
        var parts = BuildConfirm(owner, title, message, confirmText, danger);
        return parts.Window.ShowDialog() == true;   // only the confirm button sets DialogResult true
    }

    /// <summary>The confirm dialog's parts, for a WPF test that must not show the modal (polish review 2026-09-10 F-8).</summary>
    internal sealed record ConfirmParts(Window Window, Button Confirm, Button Cancel);

    internal static ConfirmParts BuildConfirm(Window? owner, string title, string message, string confirmText, bool danger)
    {
        var win = BuildShell(owner, title, out var body);
        body.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var confirm = new Button { Content = confirmText, Style = Style(danger ? "DangerButton" : "AccentButton"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = Style("DarkButton"), MinWidth = 90, IsCancel = true, IsDefault = true };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        confirm.Click += (_, _) => { win.DialogResult = true; };
        return new ConfirmParts(win, confirm, cancel);
    }

    /// <summary>Themed dark message dialog with a single OK button (done / not-ready / failed notices).</summary>
    public static void ShowInfo(Window owner, string title, string message)
    {
        var win = BuildShell(owner, title, out var body);
        body.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var ok = new Button { Content = "OK", Style = Style("AccentButton"), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => { win.DialogResult = true; };
        body.Children.Add(ok);

        win.ShowDialog();
    }
}
