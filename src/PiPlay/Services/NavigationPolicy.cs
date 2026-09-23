namespace PiPlay.Services;

/// <summary>Which PiPlay surface a navigation decision is being made for.</summary>
public enum NavigationSurface
{
    /// <summary>Main browsing window.</summary>
    Source,

    /// <summary>Popout Player - a focused media surface.</summary>
    Player,
}

/// <summary>
/// The shared allowlist policy from spec 15.2, used by both windows and both event handlers
/// (NavigationStarting and NewWindowRequested) so the rules can't drift.
///
/// Intent: the allowlist is a guardrail that stops the WebView wandering onto unrelated sites
/// (stray links, ad click-throughs) - it is NOT a hard security boundary and must not block a
/// legitimate Google sign-in. We therefore allow YouTube plus Google's sign-in/account
/// subdomains on every surface, and open everything else in the system browser.
/// </summary>
public static class NavigationPolicy
{
    /// <summary>
    /// Virtual-host name for the local compact-player shell (spec 10.3), served from a WebView2
    /// folder mapping. The single source of truth for the host: <see cref="WebViewEnvironmentService"/>
    /// derives the shell origin/URL from it, and the allowlist permits it on the Popout Player only.
    /// </summary>
    public const string ShellHost = "piplay.local";

    // Google sign-in / account subdomains used during YouTube login. Matched across ANY
    // top-level domain so regional flows (e.g. accounts.google.no, consent.google.de) are not
    // bounced out to the system browser.
    private static readonly string[] GoogleAuthSubdomains =
    {
        "accounts", "signin", "myaccount", "consent",
    };

    /// <summary>True if the URI may be navigated to inside the given PiPlay surface.</summary>
    public static bool IsAllowed(Uri? uri, NavigationSurface surface)
    {
        if (uri is null) return false;

        // In-app / runtime schemes used by WebView2 itself.
        if (uri.Scheme is "about" or "data" or "blob") return true;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host.ToLowerInvariant();

        // YouTube and Google login are allowed on both surfaces: a sign-in redirect must never
        // dead-end the player either.
        if (IsYouTubeHost(host) || IsGoogleAuthHost(host)) return true;

        // The local compact-player shell (spec 10.3) is allowed on the Popout Player only — it never
        // loads on the Source Window. Exact host match (not a broad ".local") so nothing else local
        // is permitted. The shell's nested youtube.com iframe is a frame navigation, not affected by
        // this top-level allowlist.
        return surface == NavigationSurface.Player && host == ShellHost;
    }

    public static bool IsYouTubeHost(string host) =>
        host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal)
        || host == "youtu.be"
        || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);

    /// <summary>
    /// True for Google sign-in/account hosts on any TLD:
    /// <c>&lt;accounts|signin|myaccount|consent&gt;.google.&lt;tld&gt;</c>, including two-label
    /// suffixes such as <c>co.uk</c>. Rejects look-alikes like <c>accounts.google.com.evil.test</c>.
    /// </summary>
    public static bool IsGoogleAuthHost(string host)
    {
        var labels = host.Split('.');
        if (labels.Length < 3) return false;
        if (labels[1] != "google") return false;
        if (Array.IndexOf(GoogleAuthSubdomains, labels[0]) < 0) return false;
        return IsPlausibleTldSuffix(labels, 2);
    }

    // Labels from startIndex to the end must look like a public suffix: one or two purely
    // alphabetic labels of 2-3 chars (com, no, co, uk, de, ...). This is what keeps a
    // look-alike host (extra registrable labels after the suffix) from matching.
    private static bool IsPlausibleTldSuffix(string[] labels, int startIndex)
    {
        var count = labels.Length - startIndex;
        if (count is < 1 or > 2) return false;

        for (var i = startIndex; i < labels.Length; i++)
        {
            var label = labels[i];
            if (label.Length is < 2 or > 3) return false;
            foreach (var c in label)
                if (c is < 'a' or > 'z') return false;
        }

        return true;
    }

    /// <summary>
    /// A failed completion for a navigation that is provably not the latest one started on the core
    /// belongs to a navigation a newer one replaced (a reload issued while the previous reload is
    /// still pending). It rendered nothing, so the reload settle bound (spec 15.4), the restart
    /// notice, and a queued return replay (spec 14) all wait for the newer navigation's own
    /// completion. A success is never superseded; a failure for the latest navigation settles the
    /// page (WebView2's error page, or the page's own window.stop(), which WebView2 reports as
    /// ConnectionAborted on a rendered page); with no start recorded nothing is skipped.
    /// </summary>
    public static bool IsSupersededCompletion(bool isSuccess, ulong navigationId, ulong latestStartedNavigationId)
        => !isSuccess && latestStartedNavigationId != 0 && navigationId != latestStartedNavigationId;
}
