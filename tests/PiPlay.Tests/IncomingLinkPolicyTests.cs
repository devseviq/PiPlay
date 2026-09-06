using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>
/// Receiving-side contract for links delivered by a second launch or the startup argument
/// (REQ-APP-01, ADR-0009). The matrix here is the specification; MainWindow tests only prove the
/// window applies each decision.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class IncomingLinkPolicyTests
{
    private const string VideoLink = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private const string PlaylistLink = "https://www.youtube.com/playlist?list=PL1234567890";

    private static IncomingLinkState Idle(bool browserReady = true) => new(
        BrowserReady: browserReady, PopoutActive: false, PopoutInProgress: false,
        ReturnInProgress: false, ClearingBrowserData: false, Closing: false);

    [Fact]
    public void A_ready_source_without_a_popout_navigates()
    {
        var decision = IncomingLinkPolicy.Decide(VideoLink, Idle());

        Assert.Equal(IncomingLinkAction.NavigateSource, decision.Action);
        Assert.Equal("dQw4w9WgXcQ", decision.Target!.VideoId);
        Assert.True(decision.Accepted);
    }

    [Fact]
    public void A_target_before_browser_readiness_is_queued_not_dropped()
    {
        var decision = IncomingLinkPolicy.Decide(VideoLink, Idle(browserReady: false));

        Assert.Equal(IncomingLinkAction.QueueUntilReady, decision.Action);
        Assert.True(decision.Accepted);
    }

    [Fact]
    public void A_video_link_retargets_an_active_popout_instead_of_the_hidden_source()
    {
        var decision = IncomingLinkPolicy.Decide(VideoLink, Idle() with { PopoutActive = true });

        Assert.Equal(IncomingLinkAction.RetargetPopout, decision.Action);
        Assert.Equal("dQw4w9WgXcQ", decision.Target!.VideoId);
    }

    [Fact]
    public void A_playlist_only_link_waits_for_the_source_while_a_popout_is_active()
    {
        var decision = IncomingLinkPolicy.Decide(PlaylistLink, Idle() with { PopoutActive = true });

        Assert.Equal(IncomingLinkAction.Retain, decision.Action);
        Assert.True(decision.Target!.IsPlaylistOnly);
        Assert.Equal("PL1234567890", decision.Target.PlaylistId);
    }

    [Fact]
    public void A_playlist_only_link_navigates_a_source_that_owns_playback()
    {
        Assert.Equal(IncomingLinkAction.NavigateSource, IncomingLinkPolicy.Decide(PlaylistLink, Idle()).Action);
    }

    [Theory]
    [InlineData(true, false, false)]   // launching
    [InlineData(false, true, false)]   // returning (including an awaited return replay)
    [InlineData(false, false, true)]   // clearing browser data
    [InlineData(true, false, true)]    // overlapping transitions
    public void Links_arriving_mid_transition_are_retained_until_ownership_is_stable(
        bool popoutInProgress, bool returnInProgress, bool clearing)
    {
        var state = Idle() with
        {
            PopoutActive = popoutInProgress,   // a launch may already have created the player
            PopoutInProgress = popoutInProgress,
            ReturnInProgress = returnInProgress,
            ClearingBrowserData = clearing,
        };

        var decision = IncomingLinkPolicy.Decide(VideoLink, state);

        Assert.Equal(IncomingLinkAction.Retain, decision.Action);
        Assert.Equal("dQw4w9WgXcQ", decision.Target!.VideoId);
        Assert.True(decision.Accepted);
    }

    [Fact]
    public void A_transition_while_the_browser_is_not_ready_still_retains()
    {
        var state = Idle(browserReady: false) with { ReturnInProgress = true };

        Assert.Equal(IncomingLinkAction.Retain, IncomingLinkPolicy.Decide(VideoLink, state).Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_url_activation_only_brings_the_app_forward(string? payload)
    {
        var decision = IncomingLinkPolicy.Decide(payload, Idle() with { PopoutActive = true });

        Assert.Equal(IncomingLinkAction.ActivateOnly, decision.Action);
        Assert.Null(decision.Target);
        Assert.True(decision.Accepted);
    }

    [Theory]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("--verbose")]
    [InlineData("%E2%82%")]
    [InlineData("https://www.youtube.com/watch?v=%")]
    public void Unsupported_payloads_are_rejected_at_the_receiving_boundary(string payload)
    {
        var decision = IncomingLinkPolicy.Decide(payload, Idle());

        Assert.Equal(IncomingLinkAction.Reject, decision.Action);
        Assert.Null(decision.Target);
        Assert.False(decision.Accepted);
    }

    [Theory]
    [InlineData(VideoLink)]
    [InlineData(null)]
    public void A_closing_window_is_unavailable_not_a_rejection_so_the_sender_elects_a_replacement(string? payload)
    {
        var decision = IncomingLinkPolicy.Decide(payload, Idle() with { Closing = true });

        Assert.Equal(IncomingLinkAction.Unavailable, decision.Action);
        Assert.Null(decision.Target);
        Assert.False(decision.Accepted);
    }
}
