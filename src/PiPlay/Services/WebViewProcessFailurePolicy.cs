using Microsoft.Web.WebView2.Core;

namespace PiPlay.Services;

/// <summary>Product-level grouping of WebView2 process failures (spec 15.4, PP-02).</summary>
public enum WebViewFailureKind
{
    /// <summary>The page's renderer died; the core is alive and can reload the page.</summary>
    RendererExited,

    /// <summary>The renderer is hung. WebView2 may recover it or follow up with an exit.</summary>
    RendererUnresponsive,

    /// <summary>The browser process is gone: every core on the shared environment is dead.</summary>
    BrowserProcessExited,

    /// <summary>A subframe renderer, GPU, utility, sandbox, or plugin helper died; WebView2 restarts these itself.</summary>
    HelperProcessExited,
}

public enum WebViewRecoveryAction
{
    /// <summary>Nothing to do: a helper restarted itself, or the failure is only worth a log line.</summary>
    LogOnly,

    /// <summary>Reload the page on the live core.</summary>
    Reload,

    /// <summary>Dispose the control and create a new one on the environment.</summary>
    Recreate,

    /// <summary>Consecutive failures exhausted the automatic budget: show the failed state, wait for Retry.</summary>
    GiveUp,

    /// <summary>A recovery is already running or the window is closing: this notification is coalesced.</summary>
    Ignore,
}

/// <summary>
/// One decision for both surfaces (spec 15.4, PP-02). A renderer exit reloads; a browser-process
/// exit needs a new control (the Source recreates its own, the Popout closes and lets the Source
/// own playback); self-recovering helpers are logged. One recovery runs at a time, and the
/// automatic budget is bounded so a crash loop ends in a visible failed state instead of a
/// flicker. Follows Microsoft's process-related-events guidance.
/// </summary>
public static class WebViewProcessFailurePolicy
{
    /// <summary>Automatic recoveries allowed in a row before the failed state waits for Retry.</summary>
    public const int MaxConsecutiveRecoveries = 3;

    /// <summary>A failure this long after the previous one starts a fresh budget.</summary>
    public static readonly TimeSpan StabilityWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Upper bound on one return transition (spec 14): a Navigate whose completion never arrives,
    /// or a replay that hangs, releases the Source commands after this.
    /// </summary>
    public static readonly TimeSpan ReturnTransitionDeadline = TimeSpan.FromSeconds(20);

    public static WebViewFailureKind Classify(CoreWebView2ProcessFailedKind kind) => kind switch
    {
        CoreWebView2ProcessFailedKind.BrowserProcessExited => WebViewFailureKind.BrowserProcessExited,
        CoreWebView2ProcessFailedKind.RenderProcessExited => WebViewFailureKind.RendererExited,
        CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => WebViewFailureKind.RendererUnresponsive,
        _ => WebViewFailureKind.HelperProcessExited,
    };

    /// <summary>
    /// <paramref name="recoveryInProgress"/> is the recovery already running (Reload or Recreate),
    /// or null. A recreate swallows every duplicate; a reload swallows only another renderer exit,
    /// because the browser process can die while the reload is still pending and a dead core is
    /// not something a reload ever recovers from.
    /// </summary>
    public static WebViewRecoveryAction Decide(
        WebViewFailureKind kind, int consecutiveRecoveries, WebViewRecoveryAction? recoveryInProgress, bool closing)
    {
        if (closing) return WebViewRecoveryAction.Ignore;
        switch (kind)
        {
            case WebViewFailureKind.RendererUnresponsive:
            case WebViewFailureKind.HelperProcessExited:
                return WebViewRecoveryAction.LogOnly;
            case WebViewFailureKind.RendererExited:
            case WebViewFailureKind.BrowserProcessExited:
                if (recoveryInProgress == WebViewRecoveryAction.Recreate) return WebViewRecoveryAction.Ignore;
                if (recoveryInProgress == WebViewRecoveryAction.Reload && kind == WebViewFailureKind.RendererExited)
                    return WebViewRecoveryAction.Ignore;
                if (consecutiveRecoveries >= MaxConsecutiveRecoveries) return WebViewRecoveryAction.GiveUp;
                return kind == WebViewFailureKind.RendererExited
                    ? WebViewRecoveryAction.Reload
                    : WebViewRecoveryAction.Recreate;
            default:
                return WebViewRecoveryAction.LogOnly;
        }
    }

    /// <summary>The consecutive count this failure belongs to: continues the streak or starts over.</summary>
    public static int ConsecutiveCountFor(int previousCount, DateTimeOffset? previousFailure, DateTimeOffset now) =>
        previousFailure is { } last && now - last < StabilityWindow ? previousCount : 0;

    /// <summary>Failure-specific heading and body for the Source failure panel.</summary>
    public static (string Heading, string Message) Describe(WebViewFailureKind kind, WebViewRecoveryAction action) =>
        action switch
        {
            WebViewRecoveryAction.GiveUp => (
                "The browser keeps failing",
                "PiPlay stopped restarting it automatically. Click Retry to try again, or close and reopen PiPlay."),
            WebViewRecoveryAction.Recreate => (
                "The browser component stopped",
                "PiPlay is restarting it and returning to the page you were on."),
            WebViewRecoveryAction.Reload => (
                "YouTube stopped responding",
                "PiPlay is reloading the page."),
            _ => kind == WebViewFailureKind.RendererUnresponsive
                ? ("YouTube is not responding", "PiPlay is waiting for the page to recover.")
                : ("The browser component stopped", "PiPlay is restarting it."),
        };

    /// <summary>Heading and body while a Retry or automatic recreate is bringing the browser up.</summary>
    public static (string Heading, string Message) DescribeRestarting() =>
        ("Restarting the browser", "PiPlay is starting the browser component again.");

    /// <summary>The Popout's one-line notice for a page reload after its renderer died.</summary>
    public const string PopoutReloadMessage = "The video page stopped responding. PiPlay is reloading it.";
}
