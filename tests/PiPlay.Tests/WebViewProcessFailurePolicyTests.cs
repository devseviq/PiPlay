using Microsoft.Web.WebView2.Core;
using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>
/// Coordinated WebView2 process-failure policy (spec 15.4, review 2026-09-05 PP-02): renderer
/// exits reload, browser-process exits recreate, helpers are left to WebView2, one recovery at a
/// time, and a crash loop ends in a visible failed state.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class WebViewProcessFailurePolicyTests
{
    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited, WebViewFailureKind.BrowserProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited, WebViewFailureKind.RendererExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive, WebViewFailureKind.RendererUnresponsive)]
    [InlineData(CoreWebView2ProcessFailedKind.FrameRenderProcessExited, WebViewFailureKind.HelperProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited, WebViewFailureKind.HelperProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited, WebViewFailureKind.HelperProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited, WebViewFailureKind.HelperProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiPluginProcessExited, WebViewFailureKind.HelperProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited, WebViewFailureKind.HelperProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.UnknownProcessExited, WebViewFailureKind.HelperProcessExited)]
    public void Every_WebView2_failure_kind_maps_to_one_product_kind(CoreWebView2ProcessFailedKind wire, WebViewFailureKind expected)
    {
        Assert.Equal(expected, WebViewProcessFailurePolicy.Classify(wire));
    }

    [Theory]
    [InlineData(WebViewFailureKind.RendererExited, WebViewRecoveryAction.Reload)]
    [InlineData(WebViewFailureKind.BrowserProcessExited, WebViewRecoveryAction.Recreate)]
    [InlineData(WebViewFailureKind.RendererUnresponsive, WebViewRecoveryAction.LogOnly)]
    [InlineData(WebViewFailureKind.HelperProcessExited, WebViewRecoveryAction.LogOnly)]
    public void A_fresh_failure_chooses_the_intended_recovery(WebViewFailureKind kind, WebViewRecoveryAction expected)
    {
        Assert.Equal(expected, WebViewProcessFailurePolicy.Decide(kind, consecutiveRecoveries: 0, recoveryInProgress: null, closing: false));
    }

    [Theory]
    [InlineData(WebViewFailureKind.RendererExited, WebViewRecoveryAction.Reload)]
    [InlineData(WebViewFailureKind.RendererExited, WebViewRecoveryAction.Recreate)]
    [InlineData(WebViewFailureKind.BrowserProcessExited, WebViewRecoveryAction.Recreate)]
    public void Duplicate_notifications_during_a_recovery_are_coalesced(WebViewFailureKind kind, WebViewRecoveryAction inProgress)
    {
        Assert.Equal(WebViewRecoveryAction.Ignore,
            WebViewProcessFailurePolicy.Decide(kind, 0, recoveryInProgress: inProgress, closing: false));
    }

    [Fact]
    public void A_browser_exit_during_a_reload_is_not_coalesced_because_a_reload_cannot_revive_a_dead_core()
    {
        Assert.Equal(WebViewRecoveryAction.Recreate,
            WebViewProcessFailurePolicy.Decide(WebViewFailureKind.BrowserProcessExited, 0,
                recoveryInProgress: WebViewRecoveryAction.Reload, closing: false));
    }

    [Theory]
    [InlineData(WebViewFailureKind.RendererExited)]
    [InlineData(WebViewFailureKind.BrowserProcessExited)]
    [InlineData(WebViewFailureKind.HelperProcessExited)]
    public void A_closing_window_never_starts_a_recovery(WebViewFailureKind kind)
    {
        Assert.Equal(WebViewRecoveryAction.Ignore,
            WebViewProcessFailurePolicy.Decide(kind, 0, recoveryInProgress: null, closing: true));
    }

    [Theory]
    [InlineData(WebViewFailureKind.RendererExited)]
    [InlineData(WebViewFailureKind.BrowserProcessExited)]
    public void The_automatic_budget_ends_in_a_visible_failed_state(WebViewFailureKind kind)
    {
        var last = WebViewProcessFailurePolicy.MaxConsecutiveRecoveries - 1;
        Assert.NotEqual(WebViewRecoveryAction.GiveUp, WebViewProcessFailurePolicy.Decide(kind, last, null, false));
        Assert.Equal(WebViewRecoveryAction.GiveUp,
            WebViewProcessFailurePolicy.Decide(kind, WebViewProcessFailurePolicy.MaxConsecutiveRecoveries, null, false));
    }

    [Fact]
    public void Consecutive_count_continues_inside_the_stability_window_and_restarts_after_it()
    {
        var t0 = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(0, WebViewProcessFailurePolicy.ConsecutiveCountFor(2, previousFailure: null, t0));
        Assert.Equal(2, WebViewProcessFailurePolicy.ConsecutiveCountFor(2, t0, t0 + TimeSpan.FromSeconds(5)));
        Assert.Equal(0, WebViewProcessFailurePolicy.ConsecutiveCountFor(2, t0, t0 + WebViewProcessFailurePolicy.StabilityWindow));
    }

    [Fact]
    public void Each_failure_kind_and_outcome_gets_its_own_heading()
    {
        var headings = new[]
        {
            WebViewProcessFailurePolicy.Describe(WebViewFailureKind.RendererExited, WebViewRecoveryAction.Reload).Heading,
            WebViewProcessFailurePolicy.Describe(WebViewFailureKind.BrowserProcessExited, WebViewRecoveryAction.Recreate).Heading,
            WebViewProcessFailurePolicy.Describe(WebViewFailureKind.BrowserProcessExited, WebViewRecoveryAction.GiveUp).Heading,
            WebViewProcessFailurePolicy.DescribeRestarting().Heading,
        };

        Assert.Equal(headings.Length, headings.Distinct().Count());
        Assert.All(headings, h => Assert.False(string.IsNullOrWhiteSpace(h)));
        Assert.DoesNotContain("WebView2 Runtime is required", headings);   // a crash is not a missing runtime
    }

    [Fact]
    public void Return_transition_deadline_is_bounded_and_longer_than_the_replay_loop_plus_the_ad_wait()
    {
        // The replay loop polls the video element 12 x 250 ms and a return that lands on an ad
        // then holds its seek for the PP-03 wait; the deadline must outlast both and still
        // release the Source commands long before a user gives up.
        var replayLoop = TimeSpan.FromMilliseconds(12 * 250);
        Assert.True(WebViewProcessFailurePolicy.ReturnTransitionDeadline > replayLoop + ReturnReplayAdPolicy.WaitBound + TimeSpan.FromSeconds(2));
        Assert.True(WebViewProcessFailurePolicy.ReturnTransitionDeadline <= TimeSpan.FromSeconds(30));
    }
}
