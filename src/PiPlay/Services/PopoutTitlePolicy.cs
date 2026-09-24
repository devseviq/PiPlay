using System.Text.RegularExpressions;

namespace PiPlay.Services;

/// <summary>
/// The Popout's window title names what is playing, so the taskbar, Alt+Tab, and screen readers
/// can tell it apart from the Source Window. YouTube's document title carries a notification
/// count prefix and a site suffix ("(3) Video - YouTube"); both are noise here. The title is
/// display-only and never logged (spec 18).
/// </summary>
public static partial class PopoutTitlePolicy
{
    public const string FallbackTitle = "PiPlay Video Popout";
    public const int MaxVideoTitleLength = 120;

    /// <summary>
    /// Title for the Popout window. Only an https YouTube document names it; the local compact
    /// shell, sign-in pages, and error pages keep <see cref="FallbackTitle"/>.
    /// </summary>
    public static string Format(string? documentTitle, string? documentSource)
    {
        if (!Uri.TryCreate(documentSource, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps
            || !NavigationPolicy.IsYouTubeHost(source.Host.ToLowerInvariant()))
            return FallbackTitle;
        if (string.IsNullOrWhiteSpace(documentTitle)) return FallbackTitle;

        var title = ControlCharacters().Replace(documentTitle, " ").Trim();
        title = NotificationCountPrefix().Replace(title, string.Empty);
        title = SiteSuffix().Replace(title, string.Empty).Trim();

        // A bare site name (home, loading, player-shell pages) says nothing about the video.
        if (title.Length == 0
            || title.Equals("YouTube", StringComparison.OrdinalIgnoreCase)
            || title.Equals("YouTube Music", StringComparison.OrdinalIgnoreCase))
            return FallbackTitle;

        if (title.Length > MaxVideoTitleLength)
            title = title[..(MaxVideoTitleLength - 1)].TrimEnd() + "…";

        return $"{title} — PiPlay";
    }

    [GeneratedRegex(@"^\(\d+\+?\)\s*")]
    private static partial Regex NotificationCountPrefix();

    [GeneratedRegex(@"\s+-\s+YouTube(\s+Music)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SiteSuffix();

    [GeneratedRegex(@"\p{Cc}+")]
    private static partial Regex ControlCharacters();
}
