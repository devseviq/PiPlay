using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using PiPlay;
using PiPlay.Models;
using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>
/// Polish review 2026-09-10 (docs/reviews/review-controller-2026-09-10-piplay-polish.md), the
/// Source Window findings: the failure panel's action hierarchy and its queued-work notes (F-3,
/// F-4), honest Back/Reload while no core is live (F-5), the compact toolbar's transition labels and
/// the tooltips of disabled actions (F-6), the unsaved-settings hint's lifetime (F-9), and the
/// placeholder beneath the failure panel (F-10). Headless: no WebView2 core is ever created.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Wpf)]
public class MainWindowPolishTests
{
    private const string VideoA = "AAAAAAAAAAA";
    private const string LinkB = "https://www.youtube.com/watch?v=BBBBBBBBBBB";

    private static T Named<T>(Window window, string name) where T : class =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static Style AppStyle(string key) => (Style)Application.Current.FindResource(key);

    /// <summary>Drive the real crash loop to the terminal failed state (the GiveUp path).</summary>
    private static void GiveUp(MainWindow window)
    {
        window.SetBrowserReadyForTests(true);
        var t0 = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < WebViewProcessFailurePolicy.MaxConsecutiveRecoveries; i++)
        {
            window.HandleSourceProcessFailure(WebViewFailureKind.RendererExited, t0 + TimeSpan.FromSeconds(i));
            window.ReleaseBrowserStateAfterNavigationForTests();
        }
        Assert.Equal(WebViewRecoveryAction.GiveUp,
            window.HandleSourceProcessFailure(WebViewFailureKind.RendererExited, t0 + TimeSpan.FromSeconds(10)));
        Assert.True(window.BrowserFailedForTests);
    }

    // --- F-3: the failure panel's action hierarchy ---

    [Fact]
    public void Runtime_missing_leads_with_the_download_and_demotes_retry() => StaTestThread.Invoke(() =>
    {
        // Without the runtime, Retry cannot succeed: the download is the one useful action and
        // wears the accent; Retry stays for after the install as the quiet secondary.
        var window = new MainWindow();
        window.ShowBrowserStateForTests("WebView2 Runtime is required", "Install it, then click Retry.",
            retryEnabled: true, link: RuntimeLinkMode.Primary);

        var download = Named<Button>(window, "RuntimeDownloadButton");
        var retry = Named<Button>(window, "RuntimeRetryButton");
        Assert.Equal(Visibility.Visible, download.Visibility);
        Assert.Same(AppStyle("AccentButton"), download.Style);
        Assert.Same(AppStyle("DarkButton"), retry.Style);
        Assert.True(retry.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace((string)download.ToolTip));
        Assert.False(string.IsNullOrWhiteSpace((string)retry.ToolTip));
        Assert.Same(download, ((Panel)download.Parent).Children[0]);
    });

    [Fact]
    public void Give_up_keeps_retry_primary_and_the_download_as_a_secondary_way_out() => StaTestThread.Invoke(() =>
    {
        // The runtime is installed and merely keeps crashing: Retry is the primary action again and
        // the download stays reachable (a broken runtime install is one plausible cause) but quiet.
        var window = new MainWindow();
        GiveUp(window);

        var download = Named<Button>(window, "RuntimeDownloadButton");
        var retry = Named<Button>(window, "RuntimeRetryButton");
        Assert.Equal(Visibility.Visible, download.Visibility);
        Assert.Same(AppStyle("DarkButton"), download.Style);
        Assert.Same(AppStyle("AccentButton"), retry.Style);
        Assert.True(retry.IsEnabled);
        Assert.Same(retry, ((Panel)retry.Parent).Children[0]);
    });

    [Fact]
    public void Transient_notices_hide_the_download_and_explain_the_disabled_retry() => StaTestThread.Invoke(() =>
    {
        // A reload or restart is already running: a download link is noise, and a greyed Retry
        // with no explanation reads as broken. The tooltip must show on the disabled button.
        var window = new MainWindow();
        window.SetBrowserReadyForTests(true);
        Assert.Equal(WebViewRecoveryAction.Reload, window.HandleSourceProcessFailure(WebViewFailureKind.RendererExited));

        var download = Named<Button>(window, "RuntimeDownloadButton");
        var retry = Named<Button>(window, "RuntimeRetryButton");
        Assert.Equal(Visibility.Collapsed, download.Visibility);
        Assert.False(retry.IsEnabled);
        Assert.True(ToolTipService.GetShowOnDisabled(retry));
        Assert.Contains("finishes", (string)retry.ToolTip);

        window.ReleaseBrowserStateAfterNavigationForTests();
        Assert.False(window.IsRuntimeErrorPanelVisibleForTests);
    });

    // --- F-4: the failed panel says what is waiting for Retry ---

    [Fact]
    public void Failed_panel_names_the_video_waiting_for_retry() => StaTestThread.Invoke(() =>
    {
        // A return that lands on a failed browser is queued silently (spec 14, ADR-0010); the
        // panel must say the video comes back with Retry, or the user assumes it was lost.
        var window = new MainWindow();
        GiveUp(window);
        Assert.Null(window.RuntimeErrorNoteForTests);

        window.ApplyReturnActionAsync(new PlayerReturnState { VideoId = VideoA, LastKnownSeconds = 42 })
            .GetAwaiter().GetResult();

        Assert.NotNull(window.PendingReturnReplayForTests);
        Assert.Equal(MainWindow.VideoWaitingNote, window.RuntimeErrorNoteForTests);
    });

    [Fact]
    public void Failed_panel_names_the_page_waiting_for_retry() => StaTestThread.Invoke(() =>
    {
        // A link or a typed address that arrives while the browser is down opens with Retry
        // (spec 15.4 keeps the URL box live); the panel says so instead of swallowing it.
        var window = new MainWindow();
        GiveUp(window);

        Assert.Equal(IncomingLinkAction.QueueUntilReady, window.AcceptIncomingLink(LinkB).Action);
        Assert.Equal(LinkB, window.PendingUrlForTests);
        Assert.Equal(MainWindow.PageWaitingNote, window.RuntimeErrorNoteForTests);

        window.NavigateForTests("https://www.youtube.com/");
        Assert.Equal(MainWindow.PageWaitingNote, window.RuntimeErrorNoteForTests);
    });

    [Fact]
    public void A_newer_link_outranks_the_queued_video_in_the_note() => StaTestThread.Invoke(() =>
    {
        // The pending URL is what the replacement core opens; a queued snapshot for another video
        // will be dropped on that navigation (ClearStalePendingReturnReplay), so the note follows the page.
        var window = new MainWindow();
        GiveUp(window);
        window.ApplyReturnActionAsync(new PlayerReturnState { VideoId = VideoA }).GetAwaiter().GetResult();
        Assert.Equal(MainWindow.VideoWaitingNote, window.RuntimeErrorNoteForTests);

        window.AcceptIncomingLink(LinkB);

        Assert.Equal(MainWindow.PageWaitingNote, window.RuntimeErrorNoteForTests);
    });

    [Fact]
    public void The_note_leaves_with_the_panel() => StaTestThread.Invoke(() =>
    {
        var window = new MainWindow();
        GiveUp(window);
        window.AcceptIncomingLink(LinkB);
        Assert.NotNull(window.RuntimeErrorNoteForTests);

        window.SetBrowserReadyForTests(true);
        window.ReleaseBrowserStateAfterNavigationForTests();

        Assert.False(window.IsRuntimeErrorPanelVisibleForTests);
        Assert.Null(window.RuntimeErrorNoteForTests);
    });

    [Fact]
    public void Bring_video_back_explains_the_wait_while_the_browser_is_down() => StaTestThread.Invoke(() =>
    {
        // Bring video back stays enabled through a failure (it closes the Popout and queues the
        // return); its tooltip must say the video waits for the browser instead of promising an
        // instant return.
        var window = new MainWindow();
        var button = Named<Button>(window, "PopOutButton");

        window.ApplyPopoutActionState(hasPlayer: true);
        Assert.Equal("Return playback to the Source Window", (string)button.ToolTip);

        window.SetBrowserFailedForTests(true);
        window.ApplyPopoutActionState(hasPlayer: true);
        Assert.StartsWith("Return playback to the Source Window", (string)button.ToolTip);
        Assert.Contains("browser", (string)button.ToolTip);
        Assert.True(button.IsEnabled);

        window.SetBrowserFailedForTests(false);
        window.SetBrowserRecoveryInProgressForTests(true);
        window.ApplyPopoutActionState(hasPlayer: true);
        Assert.Contains("browser", (string)button.ToolTip);

        window.SetBrowserRecoveryInProgressForTests(false);
        window.ApplyPopoutActionState(hasPlayer: true);
        Assert.Equal("Return playback to the Source Window", (string)button.ToolTip);
    });

    // --- F-5: Back and Reload need a live core; the address box does not ---

    [Fact]
    public void Back_and_reload_wait_for_a_live_core_while_the_address_box_stays_open() => StaTestThread.Invoke(() =>
    {
        // Back/Reload call straight into CoreWebView2 and silently do nothing without one. The URL
        // box, Home and profiles queue their navigation for the next core (spec 15.4), so they stay.
        var window = new MainWindow();
        var back = Named<Button>(window, "BackButton");
        var reload = Named<Button>(window, "ReloadButton");
        var home = Named<Button>(window, "HomeButton");
        var url = Named<TextBox>(window, "UrlBox");

        Assert.False(back.IsEnabled);
        Assert.False(reload.IsEnabled);
        Assert.True(home.IsEnabled);
        Assert.True(url.IsEnabled);

        window.SetBrowserReadyForTests(true);
        Assert.True(back.IsEnabled);
        Assert.True(reload.IsEnabled);

        window.SetBrowserFailedForTests(true);
        Assert.False(back.IsEnabled);
        Assert.False(reload.IsEnabled);
        Assert.True(home.IsEnabled);
        Assert.True(url.IsEnabled);
        Assert.True(window.SourceCommandsAvailableForTests);
    });

    // --- F-6: compact toolbar transitions, and tooltips that survive IsEnabled=false ---

    [Fact]
    public void Compact_toolbar_keeps_a_short_label_during_transitions() => StaTestThread.Invoke(() =>
    {
        // The compact layout collapses the label to an icon. Ready/Open read fine as glyphs, but
        // Returning/Clearing are progress states: a lone glyph says nothing, so they keep a short label.
        var window = new MainWindow();
        var icon = Named<TextBlock>(window, "PopOutButtonIcon");
        var label = Named<TextBlock>(window, "PopOutButtonText");
        var button = Named<Button>(window, "PopOutButton");

        window.ApplySourceToolbarLayout(800);
        Assert.Equal(Visibility.Collapsed, label.Visibility);

        window.BeginReturnForTests();
        Assert.Equal(Visibility.Visible, label.Visibility);
        Assert.Equal("Returning...", label.Text);
        Assert.Equal("", icon.Text);
        Assert.Equal(new Thickness(0, 0, 8, 0), icon.Margin);
        Assert.Equal("Returning video...", System.Windows.Automation.AutomationProperties.GetName(button));

        window.ApplySourceToolbarLayout(1180);
        Assert.Equal("Returning video...", label.Text);

        window.CompleteReturnForTests();
        Assert.Equal("Pop out video", label.Text);
        window.ApplySourceToolbarLayout(800);
        Assert.Equal(Visibility.Collapsed, label.Visibility);
        Assert.Equal(new Thickness(0), icon.Margin);
    });

    [Fact]
    public void Compact_toolbar_transition_label_holds_whichever_order_the_state_and_layout_arrive() => StaTestThread.Invoke(() =>
    {
        var window = new MainWindow();
        var label = Named<TextBlock>(window, "PopOutButtonText");

        window.SetClearingBrowserDataForTests(true);
        window.ApplySourceToolbarLayout(800);
        Assert.Equal(Visibility.Visible, label.Visibility);
        Assert.Equal("Clearing...", label.Text);

        window.ApplySourceToolbarLayout(1180);
        Assert.Equal("Clearing browser data...", label.Text);
    });

    [Fact]
    public void Disabled_primary_actions_keep_their_tooltips() => StaTestThread.Invoke(() =>
    {
        // WPF hides a disabled control's tooltip by default, exactly when the user most needs to
        // know why it is disabled.
        var window = new MainWindow();
        Assert.True(ToolTipService.GetShowOnDisabled(Named<Button>(window, "PopOutButton")));
        Assert.True(ToolTipService.GetShowOnDisabled(Named<Button>(window, "RuntimeRetryButton")));

        var settings = new SettingsWindow(isBrowserReady: true);
        Assert.True(ToolTipService.GetShowOnDisabled(Named<Button>(settings, "DoneButton")));
        settings.Close();
    });

    [Fact]
    public void Pop_out_explains_browser_unavailability_and_recovers_its_ready_tooltip() => StaTestThread.Invoke(() =>
    {
        var window = new MainWindow();
        var button = Named<Button>(window, "PopOutButton");
        Assert.False(button.IsEnabled);
        Assert.Contains("browser", (string)button.ToolTip);
        window.SetBrowserReadyForTests(true);
        var readyTip = (string)button.ToolTip;
        window.SetBrowserFailedForTests(true);
        Assert.False(button.IsEnabled);
        Assert.Contains("browser", (string)button.ToolTip);
        window.SetBrowserFailedForTests(false);
        window.SetBrowserReadyForTests(true);
        Assert.Equal(readyTip, (string)button.ToolTip);
    });

    [Fact]
    public void Edit_and_delete_explain_that_they_act_on_the_selected_profile() => StaTestThread.Invoke(() =>
    {
        var window = new MainWindow(new AppSettings
        {
            Profiles = { new Profile { Name = "Saved", Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" } },
        });
        window.SetBrowserReadyForTests(true);
        var edit = Named<MenuItem>(window, "EditProfileMenuItem");
        var delete = Named<MenuItem>(window, "DeleteProfileMenuItem");
        var profiles = Named<ComboBox>(window, "ProfilesCombo");

        profiles.SelectedIndex = -1;
        Assert.False(edit.IsEnabled);
        Assert.True(ToolTipService.GetShowOnDisabled(edit));
        Assert.True(ToolTipService.GetShowOnDisabled(delete));
        Assert.Contains("profile", (string)edit.ToolTip);
        Assert.Contains("profile", (string)delete.ToolTip);
        var editDisabledTip = (string)edit.ToolTip;

        profiles.SelectedIndex = 0;
        Assert.True(edit.IsEnabled);
        Assert.NotEqual(editDisabledTip, (string)edit.ToolTip);
        Assert.Contains("profile", (string)edit.ToolTip);
    });

    [Fact]
    public void Done_explains_why_it_is_disabled_when_the_accent_is_invalid() => StaTestThread.Invoke(() =>
    {
        var settings = new SettingsWindow(isBrowserReady: true);
        try
        {
            var done = Named<Button>(settings, "DoneButton");
            var picker = Named<PiPlay.Controls.AccentColorPicker>(settings, "AccentPicker");
            var readyTip = (string)done.ToolTip;
            Assert.True(done.IsEnabled);

            picker.SelectedColor = "not-a-color";
            Assert.False(done.IsEnabled);
            Assert.NotEqual(readyTip, (string)done.ToolTip);
            Assert.Contains("valid", (string)done.ToolTip);

            picker.UseNearestReadable();
            Assert.True(done.IsEnabled);
            Assert.Equal(readyTip, (string)done.ToolTip);
        }
        finally { settings.Close(); }
    });

    // --- F-9: the unsaved-settings hint tracks the save state ---

    [Fact]
    public void Unsaved_hint_clears_once_a_later_save_succeeds_and_returns_on_a_new_refusal() => StaTestThread.Invoke(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "PiPlayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"schemaVersion\":3}");
        try
        {
            var unreadable = new SettingsService(path, _ => throw new IOException("locked"));
            unreadable.Load();
            var window = new MainWindow();
            window.ReplaceSettingsServiceForTests(unreadable);
            window.SaveSettingsForTests();
            Assert.True(window.IsSettingsUnsavedHintVisibleForTests);

            var readable = new SettingsService(path);
            readable.Load();   // a later successful read lifts the refusal (spec 12.6)
            window.ReplaceSettingsServiceForTests(readable);
            // A real sharing violation prevents atomic replacement after the read succeeded.
            using (var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                window.SaveSettingsForTests();
                Assert.True(window.IsSettingsUnsavedHintVisibleForTests);
            }
            window.SaveSettingsForTests();
            Assert.False(window.IsSettingsUnsavedHintVisibleForTests);

            var unreadableAgain = new SettingsService(path, _ => throw new IOException("locked again"));
            unreadableAgain.Load();
            window.ReplaceSettingsServiceForTests(unreadableAgain);
            window.SaveSettingsForTests();
            Assert.True(window.IsSettingsUnsavedHintVisibleForTests);   // a fresh refusal signals once more
        }
        finally
        {
            new SettingsService(path).Load();   // lift the process-wide unread flag for later tests
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    });

    [Fact]
    public void Unsaved_hint_is_hit_testable_inside_the_caption() => StaTestThread.Invoke(() =>
    {
        // The hint lives in the draggable caption; without opting into hit testing its tooltip,
        // which carries the only explanation and the way out, never shows.
        var window = new MainWindow();
        Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(Named<TextBlock>(window, "SettingsUnsavedHint")));
    });

    // --- F-10: the failure panel covers the placeholder, so the placeholder must not take input ---

    [Fact]
    public void Failure_panel_disables_the_placeholder_beneath_it() => StaTestThread.Invoke(() =>
    {
        var window = new MainWindow();
        var placeholder = Named<Border>(window, "SourcePlaceholder");
        window.ShowSourcePlaceholder(true);
        Assert.True(placeholder.IsEnabled);

        window.ShowBrowserStateForTests("Restarting the browser", "…", retryEnabled: false);
        Assert.False(placeholder.IsEnabled);
        Assert.False(Named<Button>(window, "PlaceholderBringBackButton").IsEnabled);

        window.SetBrowserReadyForTests(true);
        window.ReleaseBrowserStateAfterNavigationForTests();
        Assert.True(placeholder.IsEnabled);
    });

    // --- A-1: the pop-out failure names the window the user knows ---

    [Fact]
    public void Popout_failure_message_names_the_Source_Window()
    {
        Assert.Contains("Source Window", MainWindow.PopoutFailedBody);
        Assert.DoesNotContain("main window", MainWindow.PopoutFailedBody);
    }
}
