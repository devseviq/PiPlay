using PiPlay.Services;
using Xunit;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class KeyboardShortcutPolicyTests
{
    private const ShortcutModifiers Ctrl = ShortcutModifiers.Control;
    private const ShortcutModifiers CtrlShift = ShortcutModifiers.Control | ShortcutModifiers.Shift;

    [Theory]
    [InlineData(ShortcutKey.F6, ShortcutModifiers.None, SourceShortcut.FocusAddress)]
    [InlineData(ShortcutKey.L, Ctrl, SourceShortcut.FocusAddress)]
    [InlineData(ShortcutKey.L, CtrlShift, SourceShortcut.FocusAddress)]   // pre-existing leniency
    [InlineData(ShortcutKey.P, CtrlShift, SourceShortcut.ToggleVideoPopout)]
    [InlineData(ShortcutKey.T, Ctrl, SourceShortcut.TogglePin)]
    public void Source_maps_its_chords(ShortcutKey key, ShortcutModifiers modifiers, SourceShortcut expected) =>
        Assert.Equal(expected, KeyboardShortcutPolicy.ForSource(key, modifiers));

    [Theory]
    [InlineData(ShortcutKey.L, ShortcutModifiers.None)]         // typing "l" in the address box
    [InlineData(ShortcutKey.P, Ctrl)]                          // Ctrl+P is not the popout chord
    [InlineData(ShortcutKey.P, ShortcutModifiers.Shift)]
    [InlineData(ShortcutKey.T, CtrlShift)]
    [InlineData(ShortcutKey.T, ShortcutModifiers.None)]
    [InlineData(ShortcutKey.W, Ctrl)]                          // the Source never closes by chord
    [InlineData(ShortcutKey.F11, ShortcutModifiers.None)]
    [InlineData(ShortcutKey.Escape, ShortcutModifiers.None)]
    [InlineData(ShortcutKey.Other, Ctrl)]
    public void Source_ignores_everything_else(ShortcutKey key, ShortcutModifiers modifiers) =>
        Assert.Equal(SourceShortcut.None, KeyboardShortcutPolicy.ForSource(key, modifiers));

    [Theory]
    [InlineData(ShortcutKey.Escape, ShortcutModifiers.None, PopoutShortcut.RestoreFromExpand)]
    [InlineData(ShortcutKey.Escape, ShortcutModifiers.Shift, PopoutShortcut.RestoreFromExpand)]
    [InlineData(ShortcutKey.F11, ShortcutModifiers.None, PopoutShortcut.ToggleExpand)]
    [InlineData(ShortcutKey.T, Ctrl, PopoutShortcut.TogglePin)]
    [InlineData(ShortcutKey.W, Ctrl, PopoutShortcut.BringVideoBack)]
    [InlineData(ShortcutKey.P, CtrlShift, PopoutShortcut.BringVideoBack)]
    public void Popout_maps_its_chords(ShortcutKey key, ShortcutModifiers modifiers, PopoutShortcut expected) =>
        Assert.Equal(expected, KeyboardShortcutPolicy.ForPopout(key, modifiers));

    [Theory]
    [InlineData(ShortcutKey.F11, Ctrl)]
    [InlineData(ShortcutKey.T, ShortcutModifiers.None)]         // plain letters belong to YouTube
    [InlineData(ShortcutKey.W, ShortcutModifiers.None)]
    [InlineData(ShortcutKey.W, CtrlShift)]
    [InlineData(ShortcutKey.L, Ctrl)]                          // the Popout has no address box
    [InlineData(ShortcutKey.F6, ShortcutModifiers.None)]
    [InlineData(ShortcutKey.Other, ShortcutModifiers.None)]
    public void Popout_ignores_everything_else(ShortcutKey key, ShortcutModifiers modifiers) =>
        Assert.Equal(PopoutShortcut.None, KeyboardShortcutPolicy.ForPopout(key, modifiers));

    [Fact]
    public void A_chord_still_held_across_the_hand_over_is_latched_in_the_window_that_receives_it()
    {
        // Ctrl+Shift+P popped the video out; the Popout activates with the chord still down.
        Assert.Equal(PopoutShortcut.BringVideoBack,
            KeyboardShortcutPolicy.HeldToggleForPopout(new[] { ShortcutKey.P }, CtrlShift));
        // ...and brought it back; the Source reactivates with the chord still down.
        Assert.Equal(SourceShortcut.ToggleVideoPopout,
            KeyboardShortcutPolicy.HeldToggleForSource(new[] { ShortcutKey.P }, CtrlShift));
        Assert.Equal(PopoutShortcut.ToggleExpand,
            KeyboardShortcutPolicy.HeldToggleForPopout(new[] { ShortcutKey.F11 }, ShortcutModifiers.None));
        Assert.Equal(SourceShortcut.TogglePin,
            KeyboardShortcutPolicy.HeldToggleForSource(new[] { ShortcutKey.T }, Ctrl));
    }

    [Theory]
    [InlineData(ShortcutModifiers.None)]            // plain P is YouTube's, not a chord
    [InlineData(ShortcutModifiers.Shift)]
    [InlineData(ShortcutModifiers.Control)]
    public void Keys_held_without_their_chord_latch_nothing(ShortcutModifiers modifiers)
    {
        Assert.Equal(PopoutShortcut.None, KeyboardShortcutPolicy.HeldToggleForPopout(new[] { ShortcutKey.P }, modifiers));
        Assert.Equal(SourceShortcut.None, KeyboardShortcutPolicy.HeldToggleForSource(new[] { ShortcutKey.P }, modifiers));
        Assert.Equal(PopoutShortcut.None, KeyboardShortcutPolicy.HeldToggleForPopout(Array.Empty<ShortcutKey>(), CtrlShift));
    }

    [Fact]
    public void Gesture_hint_follows_the_label()
    {
        Assert.Equal("Pin popout on top (Ctrl+T)",
            KeyboardShortcutPolicy.WithGesture("Pin popout on top", KeyboardShortcutPolicy.TogglePinGesture));
    }

    [Fact]
    public void Repeat_gate_acts_once_per_press()
    {
        var gate = new ShortcutRepeatGate<PopoutShortcut>();

        Assert.True(gate.TryBegin(PopoutShortcut.ToggleExpand, isRepeat: false));
        // WebView2-forwarded auto-repeat reports IsRepeat == false; the latch still holds it.
        Assert.False(gate.TryBegin(PopoutShortcut.ToggleExpand, isRepeat: false));
        Assert.False(gate.TryBegin(PopoutShortcut.ToggleExpand, isRepeat: false));

        gate.Release();
        Assert.True(gate.TryBegin(PopoutShortcut.ToggleExpand, isRepeat: false));
    }

    [Fact]
    public void Repeat_gate_honours_native_repeat_and_lets_a_different_chord_through()
    {
        var gate = new ShortcutRepeatGate<PopoutShortcut>();

        Assert.False(gate.TryBegin(PopoutShortcut.TogglePin, isRepeat: true));
        Assert.True(gate.TryBegin(PopoutShortcut.TogglePin, isRepeat: false));
        Assert.True(gate.TryBegin(PopoutShortcut.ToggleExpand, isRepeat: false));
        Assert.True(gate.TryBegin(PopoutShortcut.TogglePin, isRepeat: false));
    }
}
