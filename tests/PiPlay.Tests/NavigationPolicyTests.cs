using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class NavigationPolicyTests
{
    private static Uri U(string s) => new(s);

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://m.youtube.com/")]
    [InlineData("https://music.youtube.com/")]
    [InlineData("https://youtu.be/abc")]
    [InlineData("https://www.youtube-nocookie.com/embed/abc")]
    public void Youtube_allowed_on_both_surfaces(string url)
    {
        Assert.True(NavigationPolicy.IsAllowed(U(url), NavigationSurface.Source));
        Assert.True(NavigationPolicy.IsAllowed(U(url), NavigationSurface.Player));
    }

    [Theory]
    [InlineData("https://accounts.google.com/signin")]
    [InlineData("https://consent.google.com/")]
    [InlineData("https://myaccount.google.com/")]
    [InlineData("https://signin.google.com/")]
    // Regional sign-in domains must work too - a real login must never be bounced out.
    [InlineData("https://accounts.google.no/accounts/SetSID")]
    [InlineData("https://consent.google.de/")]
    [InlineData("https://accounts.google.co.uk/")]
    public void Google_auth_allowed_on_both_surfaces(string url)
    {
        // The allowlist is a guardrail, not a blocker: Google sign-in is allowed on the Source
        // Window and the Popout Player alike so a login/consent redirect never dead-ends.
        Assert.True(NavigationPolicy.IsAllowed(U(url), NavigationSurface.Source));
        Assert.True(NavigationPolicy.IsAllowed(U(url), NavigationSurface.Player));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://www.google.com/search?q=x")]  // Google Search is not sign-in - still external.
    [InlineData("https://maps.google.com/")]
    [InlineData("https://evil.youtube.com.attacker.test/")]
    [InlineData("ftp://youtube.com/")]
    public void Other_sites_blocked_everywhere(string url)
    {
        Assert.False(NavigationPolicy.IsAllowed(U(url), NavigationSurface.Source));
        Assert.False(NavigationPolicy.IsAllowed(U(url), NavigationSurface.Player));
    }

    [Fact]
    public void Lookalike_youtube_host_is_not_allowed()
    {
        Assert.False(NavigationPolicy.IsYouTubeHost("notyoutube.com"));
        Assert.False(NavigationPolicy.IsYouTubeHost("youtube.com.evil.test"));
        Assert.True(NavigationPolicy.IsYouTubeHost("www.youtube.com"));
        Assert.True(NavigationPolicy.IsYouTubeHost("youtube.com"));
    }

    [Fact]
    public void Google_auth_host_matching_is_precise()
    {
        // Real sign-in hosts across TLDs.
        Assert.True(NavigationPolicy.IsGoogleAuthHost("accounts.google.com"));
        Assert.True(NavigationPolicy.IsGoogleAuthHost("accounts.google.no"));
        Assert.True(NavigationPolicy.IsGoogleAuthHost("accounts.google.co.uk"));

        // Look-alikes and non-auth Google hosts must not match.
        Assert.False(NavigationPolicy.IsGoogleAuthHost("accounts.google.com.evil.test"));
        Assert.False(NavigationPolicy.IsGoogleAuthHost("accounts.notgoogle.com"));
        Assert.False(NavigationPolicy.IsGoogleAuthHost("www.google.com"));
        Assert.False(NavigationPolicy.IsGoogleAuthHost("google.com"));
    }

    // --- In-app runtime schemes + null guard (relied on by both NavigationStarting handlers) ---

    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html,<p>x</p>")]
    [InlineData("blob:https://www.youtube.com/abc")]
    public void Inapp_runtime_schemes_are_allowed(string url)
    {
        Assert.True(NavigationPolicy.IsAllowed(U(url), NavigationSurface.Source));
        Assert.True(NavigationPolicy.IsAllowed(U(url), NavigationSurface.Player));
    }

    [Fact]
    public void Null_uri_is_not_allowed()
    {
        Assert.False(NavigationPolicy.IsAllowed(null, NavigationSurface.Source));
    }

    // --- Compact shell virtual host (spec 10.3): allowed on the Popout Player only ---

    [Fact]
    public void Compact_shell_host_is_allowed_on_the_player_only()
    {
        var shell = U("https://piplay.local/player.html?v=abc&start=10");
        // Without this the top-level shell navigation would be cancelled and opened externally.
        Assert.True(NavigationPolicy.IsAllowed(shell, NavigationSurface.Player));
        // The shell never loads on the Source Window.
        Assert.False(NavigationPolicy.IsAllowed(shell, NavigationSurface.Source));
    }

    [Theory]
    [InlineData("https://piplay.local.evil.test/")]   // look-alike registrable suffix
    [InlineData("https://notpiplay.local/")]
    [InlineData("https://other.local/")]              // a broad ".local" must NOT be allowed
    public void Other_or_lookalike_local_hosts_are_never_allowed(string url)
    {
        Assert.False(NavigationPolicy.IsAllowed(U(url), NavigationSurface.Player));
        Assert.False(NavigationPolicy.IsAllowed(U(url), NavigationSurface.Source));
    }


    // A failed completion for a navigation that is provably not the latest one started on the core
    // belongs to a navigation a newer one replaced (a reload issued while the previous reload is
    // still pending): it rendered nothing, so the reload settle bound, the restart notice, and a
    // queued return replay must wait for the newer navigation's own completion. A success is never
    // superseded; a failure for the latest navigation settles the page (WebView2's error page, or
    // the page's own window.stop()); with no start recorded nothing is skipped.
    [Theory]
    [InlineData(false, 1UL, 2UL, true)]
    [InlineData(false, 2UL, 2UL, false)]
    [InlineData(true, 1UL, 2UL, false)]
    [InlineData(false, 3UL, 0UL, false)]
    public void Only_a_failed_completion_for_a_navigation_a_newer_one_replaced_leaves_the_page_unsettled(
        bool isSuccess, ulong navigationId, ulong latestStartedNavigationId, bool expected)
    {
        Assert.Equal(expected, NavigationPolicy.IsSupersededCompletion(isSuccess, navigationId, latestStartedNavigationId));
    }
}
