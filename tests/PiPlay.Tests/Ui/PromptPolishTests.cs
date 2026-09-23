using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PiPlay;
using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>
/// Polish review 2026-09-10 (docs/reviews/review-controller-2026-09-10-piplay-polish.md), the
/// code-built dialogs: their inputs carry UIA names from their captions (F-2), the dormant
/// playback-mode row stays out of the profile editor (A-3), and the confirm builder styles the
/// destructive action (F-8). Built without showing the modal.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Wpf)]
public class PromptPolishTests
{
    private const string Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    private static void AssertLabelled(FrameworkElement input, string caption)
    {
        var label = Assert.IsType<TextBlock>(AutomationProperties.GetLabeledBy(input));
        Assert.Equal(caption, label.Text);
        Assert.Equal(caption, AutomationProperties.GetName(input));
    }

    [Fact]
    public void Text_prompt_input_is_labelled_by_its_message() => StaTestThread.Invoke(() =>
    {
        // A screen reader reached an unnamed edit box; the visible prompt line is its label.
        var parts = Prompt.BuildAskText(owner: null, "Save profile", "Name this profile:", initial: "");
        AssertLabelled(parts.Input, "Name this profile:");
        Assert.True(parts.Ok.IsDefault);
        Assert.Equal("Save", parts.Ok.Content);
    });

    [Fact]
    public void Profile_editor_inputs_are_labelled_by_their_captions() => StaTestThread.Invoke(() =>
    {
        var parts = Prompt.BuildEditProfile(owner: null, "Saved", Url, mode: null);
        AssertLabelled(parts.NameBox, "Name");
        AssertLabelled(parts.UrlBox, "URL");
        AssertLabelled(parts.PresentationPicker, "Popout presentation");
        Assert.Equal("Saved", parts.NameBox.Text);
        Assert.Equal(Url, parts.UrlBox.Text);
    });

    [Theory]
    [InlineData(null)]
    [InlineData("normal")]
    [InlineData("compact")]
    public void Profile_editor_hides_the_dormant_playback_mode_row_but_keeps_the_stored_token(string? mode) => StaTestThread.Invoke(() =>
    {
        // Two labels, one outcome while Compact is dormant (spec 10.2): the row hides rather than
        // offering a choice that changes nothing. The stored token still rides through the picker.
        Assert.False(PlaybackModePolicy.CompactPlayerEnabled);
        var parts = Prompt.BuildEditProfile(owner: null, "Saved", Url, mode: mode);

        Assert.Equal(Visibility.Collapsed, parts.ModeCaption.Visibility);
        Assert.Equal(Visibility.Collapsed, parts.ModePicker.Visibility);
        Assert.Equal(Visibility.Visible, parts.PresentationPicker.Visibility);
        parts.NameBox.Text = "Renamed";
        // The real save handler produces the result before WPF rejects closing an unshown modal.
        Assert.Throws<InvalidOperationException>(() => parts.Ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        var result = parts.Result();
        Assert.NotNull(result);
        Assert.Equal("Renamed", result.Value.Name);
        Assert.Equal(mode, result.Value.Mode);
    });

    [Fact]
    public void Confirm_builder_styles_the_destructive_action_and_defaults_to_cancel() => StaTestThread.Invoke(() =>
    {
        var danger = Prompt.BuildConfirm(owner: null, "Replace profile?", "…", "Replace", danger: true);
        Assert.Same(Application.Current.FindResource("DangerButton"), danger.Confirm.Style);
        Assert.Equal("Replace", danger.Confirm.Content);
        Assert.True(danger.Cancel.IsDefault);
        Assert.True(danger.Cancel.IsCancel);
        Assert.False(danger.Confirm.IsDefault);

        var plain = Prompt.BuildConfirm(owner: null, "Save?", "…", "Save", danger: false);
        Assert.Same(Application.Current.FindResource("AccentButton"), plain.Confirm.Style);
    });
}
