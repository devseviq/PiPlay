namespace PiPlay.Services;

/// <summary>The keys PiPlay's window shortcuts care about; everything else is <see cref="Other"/>.</summary>
public enum ShortcutKey
{
    Other,
    Escape,
    F6,
    F11,
    L,
    P,
    T,
    W,
}

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
}

public enum SourceShortcut
{
    None,
    FocusAddress,
    ToggleVideoPopout,
    TogglePin,
}

public enum PopoutShortcut
{
    None,
    RestoreFromExpand,
    ToggleExpand,
    TogglePin,
    BringVideoBack,
}

/// <summary>
/// Window-level keyboard shortcuts (spec 20). Only chords that WebView2 treats as accelerator keys
/// (Escape, function keys, Ctrl combinations) are mapped, because those are the keys the WPF
/// WebView2 control re-raises as WPF key events while the page has focus. Plain letters, Space,
/// and arrows always stay with YouTube's own player shortcuts. Kept free of WPF types; the windows
/// translate <c>System.Windows.Input.Key</c> through <c>KeyboardShortcutInput</c>.
/// </summary>
public static class KeyboardShortcutPolicy
{
    public const string ToggleVideoPopoutGesture = "Ctrl+Shift+P";
    public const string TogglePinGesture = "Ctrl+T";
    public const string ToggleExpandGesture = "F11";
    public const string CloseGesture = "Ctrl+W";

    public static SourceShortcut ForSource(ShortcutKey key, ShortcutModifiers modifiers) => key switch
    {
        // Pre-existing address shortcuts stay lenient about extra modifiers.
        ShortcutKey.F6 => SourceShortcut.FocusAddress,
        ShortcutKey.L when modifiers.HasFlag(ShortcutModifiers.Control) => SourceShortcut.FocusAddress,
        ShortcutKey.P when modifiers == (ShortcutModifiers.Control | ShortcutModifiers.Shift) =>
            SourceShortcut.ToggleVideoPopout,
        ShortcutKey.T when modifiers == ShortcutModifiers.Control => SourceShortcut.TogglePin,
        _ => SourceShortcut.None,
    };

    public static PopoutShortcut ForPopout(ShortcutKey key, ShortcutModifiers modifiers) => key switch
    {
        ShortcutKey.Escape => PopoutShortcut.RestoreFromExpand,
        ShortcutKey.F11 when modifiers == ShortcutModifiers.None => PopoutShortcut.ToggleExpand,
        ShortcutKey.T when modifiers == ShortcutModifiers.Control => PopoutShortcut.TogglePin,
        ShortcutKey.W when modifiers == ShortcutModifiers.Control => PopoutShortcut.BringVideoBack,
        // The same chord that popped the video out brings it back from the Popout.
        ShortcutKey.P when modifiers == (ShortcutModifiers.Control | ShortcutModifiers.Shift) =>
            PopoutShortcut.BringVideoBack,
        _ => PopoutShortcut.None,
    };

    /// <summary>
    /// The Source toggle a window that just became active must treat as already pressed: the
    /// chord that moved playback here is often still held, and its auto-repeat must not move it
    /// straight back. Only toggles count; focusing the address box again is harmless.
    /// </summary>
    public static SourceShortcut HeldToggleForSource(IEnumerable<ShortcutKey> heldKeys, ShortcutModifiers modifiers) =>
        heldKeys.Select(key => ForSource(key, modifiers))
            .FirstOrDefault(s => s is SourceShortcut.ToggleVideoPopout or SourceShortcut.TogglePin);

    /// <summary>The Popout counterpart of <see cref="HeldToggleForSource"/>; Esc restore is idempotent.</summary>
    public static PopoutShortcut HeldToggleForPopout(IEnumerable<ShortcutKey> heldKeys, ShortcutModifiers modifiers) =>
        heldKeys.Select(key => ForPopout(key, modifiers))
            .FirstOrDefault(s => s is PopoutShortcut.ToggleExpand or PopoutShortcut.TogglePin or PopoutShortcut.BringVideoBack);

    public static bool IsSourceToggleHeld(SourceShortcut shortcut, IEnumerable<ShortcutKey> heldKeys,
        ShortcutModifiers modifiers) => heldKeys.Any(key => ForSource(key, modifiers) == shortcut);

    public static bool IsPopoutToggleHeld(PopoutShortcut shortcut, IEnumerable<ShortcutKey> heldKeys,
        ShortcutModifiers modifiers) => heldKeys.Any(key => ForPopout(key, modifiers) == shortcut);

    /// <summary>Appends a gesture hint to a tooltip, e.g. "Pin on top (Ctrl+T)".</summary>
    public static string WithGesture(string text, string gesture) => $"{text} ({gesture})";
}

/// <summary>
/// Makes one physical press act once. A window that becomes active while a shortcut's keys are
/// still held latches that shortcut first, so a press that moved playback between windows is not
/// repeated by the other window. Keys forwarded from a focused WebView2 always report
/// <c>IsRepeat == false</c>, so holding F11 or Ctrl+T would otherwise flip a toggle on every
/// auto-repeat. A shortcut is latched when it acts and released by the next key-up the window
/// hears or by losing activation; releasing either the letter or Ctrl reaches the window as an
/// accelerator key-up, so the latch cannot stick between presses.
/// </summary>
public sealed class ShortcutRepeatGate<TShortcut> where TShortcut : struct, Enum
{
    private TShortcut? _held;

    /// <summary>True when this key-down should act; false for a repeat of the held shortcut.</summary>
    public bool TryBegin(TShortcut shortcut, bool isRepeat)
    {
        if (isRepeat || (_held is { } held && EqualityComparer<TShortcut>.Default.Equals(held, shortcut)))
            return false;
        _held = shortcut;
        return true;
    }

    public void ReleaseUnlessHeld(Func<TShortcut, bool> isHeld)
    {
        if (_held is { } held && !isHeld(held)) _held = null;
    }

    public void Release() => _held = null;
}
