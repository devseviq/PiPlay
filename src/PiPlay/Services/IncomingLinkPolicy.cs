using PiPlay.Models;

namespace PiPlay.Services;

/// <summary>What the Source Window does with a link delivered from outside (REQ-APP-01).</summary>
public enum IncomingLinkAction
{
    /// <summary>No target rode along: bring PiPlay forward, change nothing else.</summary>
    ActivateOnly,

    /// <summary>The payload is not a supported YouTube target.</summary>
    Reject,

    /// <summary>
    /// The Source Window is closing: nobody here can own the request. Distinct from Reject so a
    /// second launch contends for the session mutex instead of telling the user the link was bad.
    /// </summary>
    Unavailable,

    /// <summary>The browser is not ready yet: keep the target as the pending startup navigation.</summary>
    QueueUntilReady,

    /// <summary>No Popout owns playback: the Source navigates to the target.</summary>
    NavigateSource,

    /// <summary>A ready Popout owns playback: it retargets in place and takes focus (ADR-0005/0009).</summary>
    RetargetPopout,

    /// <summary>
    /// Ownership is moving (launch, return, clear) or the Popout cannot host the target: retain the
    /// newest target and apply it once the transition finishes.
    /// </summary>
    Retain,
}

/// <summary>Lifecycle snapshot the decision is made against; every flag mirrors a MainWindow guard.</summary>
public readonly record struct IncomingLinkState(
    bool BrowserReady,
    bool PopoutActive,
    bool PopoutInProgress,
    bool ReturnInProgress,
    bool ClearingBrowserData,
    bool Closing);

public readonly record struct IncomingLinkDecision(IncomingLinkAction Action, YouTubeTarget? Target)
{
    /// <summary>The sender's view: the request was taken by a running owner, not dropped.</summary>
    public bool Accepted => Action is not (IncomingLinkAction.Reject or IncomingLinkAction.Unavailable);
}

/// <summary>
/// Pure receiving-boundary decision for the single-instance hand-off and the startup argument
/// (spec 9 REQ-APP-01, 13.3, ADR-0009). The payload is revalidated with the product parser: the
/// pipe carries whatever a second process wrote, and the startup argument was accepted by the same
/// parser in another process. The hidden Source never navigates while a Popout owns playback —
/// that is what keeps the return identity comparison honest.
/// </summary>
public static class IncomingLinkPolicy
{
    public static IncomingLinkDecision Decide(string? payload, IncomingLinkState state)
    {
        if (state.Closing) return new(IncomingLinkAction.Unavailable, null);
        if (string.IsNullOrWhiteSpace(payload)) return new(IncomingLinkAction.ActivateOnly, null);
        if (!YouTubeUrlHelper.TryParse(payload, out var target)) return new(IncomingLinkAction.Reject, null);

        if (state.ClearingBrowserData || state.PopoutInProgress || state.ReturnInProgress)
            return new(IncomingLinkAction.Retain, target);

        if (state.PopoutActive)
        {
            // The Popout hosts videos only. A playlist-only page has no playable target of its own
            // (spec 13.1 resolves the first item off the Source page, which is hidden now), so it
            // waits for the Source to own playback again rather than navigating it underneath the
            // Popout.
            return string.IsNullOrEmpty(target.VideoId)
                ? new(IncomingLinkAction.Retain, target)
                : new(IncomingLinkAction.RetargetPopout, target);
        }

        return state.BrowserReady
            ? new(IncomingLinkAction.NavigateSource, target)
            : new(IncomingLinkAction.QueueUntilReady, target);
    }
}
