using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using PiPlay;
using PiPlay.Models;
using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>
/// WebView2 failure recovery on both surfaces (spec 15.4, review 2026-09-05 PP-02), headless:
/// no core is ever created, so the observable contract is the control swap, the guards, the
/// visible state, and the terminal outcome of a return in flight.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Wpf)]
public class MainWindowRecoveryTests : IDisposable
{
    private const string LinkA = "https://www.youtube.com/watch?v=AAAAAAAAAAA";

    private static PlayerWindow NewHeadlessPlayer(string url = LinkA)
    {
        var player = new PlayerWindow(
            environment: null!, url: url, topmost: false, placement: null,
            defaultWidth: 960, defaultHeight: 540, fadeEnabled: true);
        player.TrackReturnIdentity(url);
        return player;
    }

    // --- control replacement ---

    [Fact]
    public void Replacing_the_browser_keeps_its_slot_style_visibility_and_name()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            var letterbox = (Grid)window.FindName("SourceLetterbox")!;
            var old = window.BrowserForTests;
            var index = letterbox.Children.IndexOf(old);
            var placeholderIndex = letterbox.Children.IndexOf((UIElement)window.FindName("SourcePlaceholder")!);
            window.ShowSourcePlaceholder(true);   // popped out: the WebView is hidden behind the placeholder

            window.ReplaceBrowserControlForTests();

            var replacement = window.BrowserForTests;
            Assert.NotSame(old, replacement);
            Assert.Equal(index, letterbox.Children.IndexOf(replacement));
            Assert.True(index < placeholderIndex, "the browser stays below the placeholder and the failure panel");
            Assert.DoesNotContain(old, letterbox.Children.Cast<UIElement>());
            Assert.Same(old.Style, replacement.Style);
            Assert.Equal(Visibility.Hidden, replacement.Visibility);
            Assert.Same(replacement, window.FindName("Browser"));

            window.ShowSourcePlaceholder(false);
            Assert.Equal(Visibility.Visible, replacement.Visibility);   // the swap did not detach the placeholder wiring
        });
    }

    // --- decisions applied to the Source ---

    [Fact]
    public void A_renderer_exit_reloads_shows_the_reloading_state_and_ends_a_return_in_flight()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            window.BeginReturnForTests();
            window.SetPendingReturnReplayForTests(new PlayerReturnState { VideoId = "AAAAAAAAAAA", LastKnownSeconds = 3 });

            var action = window.HandleSourceProcessFailure(WebViewFailureKind.RendererExited);

            Assert.Equal(WebViewRecoveryAction.Reload, action);
            Assert.True(window.BrowserReadyForTests);   // the core is alive; only the page died
            Assert.True(window.IsRuntimeErrorPanelVisibleForTests);
            Assert.Equal("YouTube stopped responding", window.RuntimeErrorHeadingForTests);
            Assert.False(window.IsRuntimeRetryEnabledForTests);
            Assert.False(window.ReturnInProgressForTests);
            Assert.Null(window.PendingReturnReplayForTests);
            Assert.Equal(1, window.ConsecutiveBrowserRecoveriesForTests);
        });
    }

    [Fact]
    public void Helper_process_exits_and_an_unresponsive_renderer_are_logged_only()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);

            Assert.Equal(WebViewRecoveryAction.LogOnly, window.HandleSourceProcessFailure(WebViewFailureKind.HelperProcessExited));
            Assert.Equal(WebViewRecoveryAction.LogOnly, window.HandleSourceProcessFailure(WebViewFailureKind.RendererUnresponsive));

            Assert.True(window.BrowserReadyForTests);
            Assert.False(window.IsRuntimeErrorPanelVisibleForTests);
            Assert.Equal(0, window.ConsecutiveBrowserRecoveriesForTests);
        });
    }

    [Fact]
    public void Duplicate_notifications_start_one_recovery()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            window.SetBrowserRecoveryInProgressForTests(true);   // the shared environment already reported it

            Assert.Equal(WebViewRecoveryAction.Ignore, window.HandleSourceProcessFailure(WebViewFailureKind.BrowserProcessExited));
            Assert.Equal(WebViewRecoveryAction.Ignore, window.HandleSourceProcessFailure(WebViewFailureKind.RendererExited));

            Assert.False(window.IsRuntimeErrorPanelVisibleForTests);
            Assert.Equal(0, window.ConsecutiveBrowserRecoveriesForTests);
        });
    }

    [Fact]
    public void A_closing_window_ignores_failures()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            window.SetMainWindowClosingForTests(true);

            Assert.Equal(WebViewRecoveryAction.Ignore, window.HandleSourceProcessFailure(WebViewFailureKind.BrowserProcessExited));
            Assert.Same(window.BrowserForTests, window.FindName("Browser"));
            Assert.False(window.IsRuntimeErrorPanelVisibleForTests);
        });
    }

    [Fact]
    public void A_crash_loop_ends_in_the_failed_state_with_retry_enabled()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            var t0 = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

            for (var i = 0; i < WebViewProcessFailurePolicy.MaxConsecutiveRecoveries; i++)
            {
                Assert.Equal(WebViewRecoveryAction.Reload,
                    window.HandleSourceProcessFailure(WebViewFailureKind.RendererExited, t0 + TimeSpan.FromSeconds(i)));
            }

            var final = window.HandleSourceProcessFailure(WebViewFailureKind.RendererExited, t0 + TimeSpan.FromSeconds(10));

            Assert.Equal(WebViewRecoveryAction.GiveUp, final);
            Assert.False(window.BrowserReadyForTests);
            Assert.True(window.IsRuntimeErrorPanelVisibleForTests);
            Assert.Equal("The browser keeps failing", window.RuntimeErrorHeadingForTests);
            Assert.True(window.IsRuntimeRetryEnabledForTests);
            Assert.False(window.CanStartVideoPopoutForTests);

            // A failure after the stability window starts a fresh budget.
            Assert.Equal(WebViewRecoveryAction.Reload,
                window.HandleSourceProcessFailure(WebViewFailureKind.RendererExited,
                    t0 + TimeSpan.FromSeconds(10) + WebViewProcessFailurePolicy.StabilityWindow));
        });
    }

    [Fact]
    public void A_browser_process_exit_swaps_the_control_and_the_failed_environment_leaves_a_visible_retry()
    {
        StaTestThread.Invoke(() =>
        {
            // Headless: there is no PiPlay.App, so the environment step of the shared init path
            // fails synchronously. The recreate must still swap the control first, mark the
            // browser unusable, and end in the failed state with Retry enabled and no recovery
            // left running.
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            var old = window.BrowserForTests;

            var action = window.HandleSourceProcessFailure(WebViewFailureKind.BrowserProcessExited);

            Assert.Equal(WebViewRecoveryAction.Recreate, action);
            Assert.NotSame(old, window.BrowserForTests);
            Assert.False(window.BrowserReadyForTests);
            Assert.True(window.BrowserFailedForTests);
            Assert.False(window.BrowserRecoveryInProgressForTests);
            Assert.True(window.IsRuntimeErrorPanelVisibleForTests);
            Assert.True(window.IsRuntimeRetryEnabledForTests);
            Assert.Equal(1, window.ConsecutiveBrowserRecoveriesForTests);
        });
    }

    // --- return transitions always end ---

    [Fact]
    public void A_return_during_browser_recovery_queues_the_video_instead_of_scripting_a_dead_core()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserRecoveryInProgressForTests(true);   // a dead core can still be non-null

            window.ApplyReturnActionAsync(new PlayerReturnState
            {
                VideoId = "AAAAAAAAAAA", PlaylistId = "PL0123456789", LastKnownSeconds = 42,
            }).GetAwaiter().GetResult();

            Assert.Equal("https://www.youtube.com/watch?v=AAAAAAAAAAA&list=PL0123456789&t=42s", window.PendingUrlForTests);
            Assert.Null(window.PendingReturnReplayForTests);
        });
    }

    [Fact]
    public void The_return_deadline_releases_the_source_when_no_completion_arrives()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            window.BeginReturnForTests();
            window.SetPendingReturnReplayForTests(new PlayerReturnState { VideoId = "AAAAAAAAAAA" });
            Assert.True(window.IsReturnDeadlineArmedForTests);
            Assert.False(window.CanStartVideoPopoutForTests);

            window.ElapseReturnDeadlineForTests();

            Assert.False(window.ReturnInProgressForTests);
            Assert.Null(window.PendingReturnReplayForTests);
            Assert.False(window.IsReturnDeadlineArmedForTests);
            Assert.True(window.CanStartVideoPopoutForTests);
        });
    }

    [Fact]
    public void A_completed_return_disarms_the_deadline()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.BeginReturnForTests();
            window.CompleteReturnForTests();

            Assert.False(window.IsReturnDeadlineArmedForTests);
            window.ElapseReturnDeadlineForTests();   // late tick: nothing to abandon, no throw
            Assert.False(window.ReturnInProgressForTests);
        });
    }

    // --- Popout: scoped cut (renderer reloads in place, browser exit closes) ---

    [Fact]
    public void Popout_reloads_once_per_renderer_exit_and_shows_the_notice()
    {
        StaTestThread.Invoke(() =>
        {
            var player = NewHeadlessPlayer();

            Assert.Equal(WebViewRecoveryAction.Reload, player.HandleProcessFailure(WebViewFailureKind.RendererExited));
            Assert.True(player.IsErrorBarVisibleForTests);
            Assert.Equal(WebViewProcessFailurePolicy.PopoutReloadMessage, player.ErrorTextForTests);
            Assert.True(player.IsProcessRecoveryInProgressForTests);
            Assert.Equal(1, player.ConsecutiveProcessRecoveriesForTests);
            Assert.False(player.IsClosingForTests);

            // The environment fires the same failure on every core: the second is coalesced.
            Assert.Equal(WebViewRecoveryAction.Ignore, player.HandleProcessFailure(WebViewFailureKind.RendererExited));
            Assert.Equal(1, player.ConsecutiveProcessRecoveriesForTests);

            // The reload landing ends the recovery and clears the notice.
            var generation = player.BeginNavigationForTests();
            Assert.True(player.CompleteNavigationForTests(generation, succeeded: true));
            player.SettleNavigationForTests(succeeded: true);
            Assert.False(player.IsProcessRecoveryInProgressForTests);
            Assert.False(player.IsErrorBarVisibleForTests);

            // The next renderer exit is a new recovery, not a coalesced duplicate.
            Assert.Equal(WebViewRecoveryAction.Reload, player.HandleProcessFailure(WebViewFailureKind.RendererExited));
            Assert.Equal(2, player.ConsecutiveProcessRecoveriesForTests);
            player.Close();
        });
    }

    [Fact]
    public void Popout_reload_that_fails_ends_the_recovery_and_keeps_the_notice()
    {
        StaTestThread.Invoke(() =>
        {
            var player = NewHeadlessPlayer();
            player.HandleProcessFailure(WebViewFailureKind.RendererExited);

            player.SettleNavigationForTests(succeeded: false);

            Assert.False(player.IsProcessRecoveryInProgressForTests);
            Assert.True(player.IsErrorBarVisibleForTests);   // the page did not come back; the bar still offers a way out
            player.Close();
        });
    }

    [Fact]
    public void A_browser_exit_during_a_popout_reload_still_closes_the_popout()
    {
        StaTestThread.Invoke(() =>
        {
            var player = NewHeadlessPlayer();
            Assert.Equal(WebViewRecoveryAction.Reload, player.HandleProcessFailure(WebViewFailureKind.RendererExited));
            Assert.True(player.IsProcessRecoveryInProgressForTests);

            Assert.Equal(WebViewRecoveryAction.Recreate, player.HandleProcessFailure(WebViewFailureKind.BrowserProcessExited));

            Assert.True(player.IsClosingForTests);
            Assert.True(player.ReturnBrowserProcessFailedForTests);
        });
    }

    [Fact]
    public void Popout_closes_on_a_browser_process_exit_and_leaves_helpers_alone()
    {
        StaTestThread.Invoke(() =>
        {
            var player = NewHeadlessPlayer();
            var returned = 0;
            player.PlayerClosed += (_, _) => returned++;

            Assert.Equal(WebViewRecoveryAction.LogOnly, player.HandleProcessFailure(WebViewFailureKind.HelperProcessExited));
            Assert.False(player.IsClosingForTests);

            Assert.Equal(WebViewRecoveryAction.Recreate, player.HandleProcessFailure(WebViewFailureKind.BrowserProcessExited));

            Assert.True(player.IsClosingForTests);
            Assert.Equal(1, returned);   // playback returned to the Source with the last sample
            Assert.Equal(WebViewRecoveryAction.Ignore, player.HandleProcessFailure(WebViewFailureKind.RendererExited));
        });
    }

    [Fact]
    public void A_popout_that_saw_the_browser_die_first_makes_the_source_recover_before_it_returns()
    {
        StaTestThread.Invoke(() =>
        {
            // Both cores get ProcessFailed from one environment in an undefined order. When the
            // Popout's arrives first it closes and returns; the Source's own core is already dead,
            // so the return must recreate and queue the video rather than script the old core.
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            var old = window.BrowserForTests;
            var player = NewHeadlessPlayer();
            Assert.True(player.TryApplyReturnPlaybackSampleForTests(
                new PlayerState(CurrentTime: 42, Paused: false, Duration: 600, Volume: 0.5, Muted: false, PlaybackRate: 1),
                player.NavigationGenerationForTests, player.ReturnIdentityGenerationForTests, isFinalCapture: false));
            window.AttachPlayerWithReturnForTests(player);

            Assert.Equal(WebViewRecoveryAction.Recreate, player.HandleProcessFailure(WebViewFailureKind.BrowserProcessExited));

            Assert.NotSame(old, window.BrowserForTests);   // the Source recreated before acting on the return
            Assert.Equal("https://www.youtube.com/watch?v=AAAAAAAAAAA&t=42s", window.PendingUrlForTests);
            Assert.False(window.ReturnInProgressForTests);
            Assert.Null(window.PendingReturnReplayForTests);
            Assert.True(window.IsRuntimeErrorPanelVisibleForTests);   // headless: the environment step fails, Retry waits
            Assert.True(window.IsRuntimeRetryEnabledForTests);
        });
    }

    [Fact]
    public void The_reload_notice_leaves_on_any_completed_navigation_but_the_failed_state_stays()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            window.ShowBrowserStateForTests("YouTube stopped responding", "PiPlay is reloading the page.", retryEnabled: false);

            window.ReleaseBrowserStateAfterNavigationForTests();   // the reload ended, successfully or on the error page

            Assert.False(window.IsRuntimeErrorPanelVisibleForTests);
            Assert.True(window.IsRuntimeRetryEnabledForTests);

            window.SetBrowserReadyForTests(false);
            window.ShowBrowserStateForTests("The browser keeps failing", "Click Retry.", retryEnabled: true);
            window.ReleaseBrowserStateAfterNavigationForTests();   // a late completion from the old core
            Assert.True(window.IsRuntimeErrorPanelVisibleForTests);
        });
    }

    public void Dispose() => StaTestThread.Invoke(() =>
    {
        foreach (var window in Application.Current.Windows.Cast<Window>().ToArray())
        {
            try { window.Close(); }
            catch { /* Never-shown test windows can already be tearing down. */ }
        }
    });
}
