using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PiPlay.Services;

/// <summary>WPF edge of <see cref="KeyboardShortcutPolicy"/>: key translation and control invocation.</summary>
internal static class KeyboardShortcutInput
{
    public static ShortcutKey Translate(KeyEventArgs e)
    {
        // Alt chords arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key switch
        {
            Key.Escape => ShortcutKey.Escape,
            Key.F6 => ShortcutKey.F6,
            Key.F11 => ShortcutKey.F11,
            Key.L => ShortcutKey.L,
            Key.P => ShortcutKey.P,
            Key.T => ShortcutKey.T,
            Key.W => ShortcutKey.W,
            _ => ShortcutKey.Other,
        };
    }

    // The mapped keys a held chord can still repeat into a newly active window.
    private static readonly (Key Wpf, ShortcutKey Shortcut)[] ToggleKeys =
    {
        (Key.P, ShortcutKey.P), (Key.T, ShortcutKey.T), (Key.W, ShortcutKey.W), (Key.F11, ShortcutKey.F11),
    };

    /// <summary>The mapped keys physically held right now, read from the keyboard state.</summary>
    public static IEnumerable<ShortcutKey> HeldKeys() =>
        ToggleKeys.Where(k => Keyboard.IsKeyDown(k.Wpf)).Select(k => k.Shortcut).ToArray();

    public static ShortcutModifiers Translate(ModifierKeys modifiers)
    {
        var result = ShortcutModifiers.None;
        if ((modifiers & ModifierKeys.Control) != 0) result |= ShortcutModifiers.Control;
        if ((modifiers & ModifierKeys.Shift) != 0) result |= ShortcutModifiers.Shift;
        if ((modifiers & ModifierKeys.Alt) != 0) result |= ShortcutModifiers.Alt;
        return result;
    }

    /// <summary>
    /// Runs a button exactly as a click would, so every guard in its handler still applies. A
    /// disabled or hidden control is not invoked: the shortcut is only as available as the button.
    /// </summary>
    public static bool TryClick(ButtonBase button)
    {
        if (!button.IsEnabled || !button.IsVisible) return false;
        if (button is ToggleButton toggle) toggle.IsChecked = toggle.IsChecked != true;
        button.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent, button));
        return true;
    }
}
