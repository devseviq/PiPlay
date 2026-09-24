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
    /// True for Google sign-in/account hosts on Google's own domains:
    /// <c>&lt;accounts|signin|myaccount|consent&gt;.google.&lt;suffix&gt;</c>, where the suffix is
    /// <c>com</c>, a two-letter country code, or one of Google's <c>com.xx</c> / <c>co.xx</c>
    /// country domains (sign-in propagates its session through the regional domain, e.g.
    /// <c>accounts.google.no/accounts/SetSID</c>). Rejects look-alikes such as
    /// <c>accounts.google.com.evil.test</c> and hosts under a registrable domain someone else can
    /// own, such as <c>accounts.google.abc.io</c>.
    /// </summary>
    public static bool IsGoogleAuthHost(string host)
    {
        var labels = host.Split('.');
        if (labels.Length < 3) return false;
        if (labels[1] != "google") return false;
        if (Array.IndexOf(GoogleAuthSubdomains, labels[0]) < 0) return false;
        return IsGoogleDomainSuffix(labels, 2);
    }

    private static bool IsGoogleDomainSuffix(string[] labels, int startIndex) =>
        (labels.Length - startIndex) switch
        {
            1 => labels[startIndex] == "com" || IsCountryCode(labels[startIndex]),
            2 => labels[startIndex] switch
            {
                "com" => GoogleComCountryDomains.Contains(labels[startIndex + 1]),
                "co" => GoogleCoCountryDomains.Contains(labels[startIndex + 1]),
                _ => false,
            },
            _ => false,
        };

    private static bool IsCountryCode(string label) =>
        label.Length == 2 && label[0] is >= 'a' and <= 'z' && label[1] is >= 'a' and <= 'z';

    // Google's country domains that use a second-level label (google.com.au, google.co.uk, ...).
    // A missing entry only sends that region's sign-in step to the default browser.
    private static readonly HashSet<string> GoogleComCountryDomains = new(StringComparer.Ordinal)
    {
        "af", "ag", "ai", "ar", "au", "bd", "bh", "bn", "bo", "br", "bz", "co", "cu", "cy", "do",
        "ec", "eg", "et", "fj", "gh", "gi", "gt", "hk", "jm", "kh", "kw", "lb", "ly", "mm", "mt",
        "mx", "my", "na", "ng", "ni", "np", "om", "pa", "pe", "pg", "ph", "pk", "pr", "py", "qa",
        "sa", "sb", "sg", "sl", "sv", "tj", "tr", "tw", "ua", "uy", "vc", "vn",
    };

    private static readonly HashSet<string> GoogleCoCountryDomains = new(StringComparer.Ordinal)
    {
        "ao", "bw", "ck", "cr", "id", "il", "in", "jp", "ke", "kr", "ls", "ma", "mz", "nz", "th",
        "tz", "ug", "uk", "uz", "ve", "vi", "za", "zm", "zw",
    };

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
