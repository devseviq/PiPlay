namespace PiPlay.Services;

/// <summary>Whether Auto should start a Video Popout right now.</summary>
public enum AutoPopDecision
{
    /// <summary>Do nothing this tick.</summary>
    Skip,
    /// <summary>Start a Video Popout for the current video.</summary>
    Pop,
}

/// <summary>
/// Pure decision for Auto (spec §6.1), extracted from <see cref="MainWindow"/> so the
/// false-positive / anti-loop branches are testable without WebView2. Pop only when Auto is on, a
/// <c>/watch</c> video with an id is playing, it is <b>not</b> the id we last handled, and no popout is
/// already active. The "last handled id" de-dup is what blocks the return-resume re-pop loop (the return
/// path resumes source playback, which would otherwise read as a fresh play) and an in-source pause/resume
/// of the same video. (The caller clears the last-handled id when Auto is enabled, so turning Auto on
/// pops whatever is currently playing right away.)
/// </summary>
public static class AutoPopoutPolicy
{
    /// <summary>
    /// Whether the caller needs the comparatively expensive WebView player-state probe. URL and
    /// lifecycle facts are already available without crossing the WebView2 script boundary, so
    /// reject those cases first.
    /// </summary>
    public static bool NeedsPlayerState(
        bool autoEnabled,
        bool isWatchVideo,
        string? currentVideoId,
        string? lastHandledVideoId,
        bool popoutActive)
    {
        if (!autoEnabled || popoutActive) return false;
        if (!isWatchVideo || string.IsNullOrEmpty(currentVideoId)) return false;
        return !string.Equals(currentVideoId, lastHandledVideoId, StringComparison.Ordinal);
    }

    public static AutoPopDecision Decide(
        bool autoEnabled,
        bool isPlaying,
        bool isWatchVideo,
        string? currentVideoId,
        string? lastHandledVideoId,
        bool popoutActive)
    {
        if (!NeedsPlayerState(autoEnabled, isWatchVideo, currentVideoId, lastHandledVideoId, popoutActive))
            return AutoPopDecision.Skip;
        if (!isPlaying) return AutoPopDecision.Skip;
        return AutoPopDecision.Pop;
    }

    /// <summary>
    /// A Source that has left its watch page entirely (home, search, a channel) ends the
    /// once-per-video latch: navigating back to that video must auto-pop again (readiness A-5).
    /// While a popout/return transition is active the Source is the placeholder, not "left".
    /// </summary>
    public static bool ShouldResetDedupOnSourceDeparture(
        bool isWatchVideo, bool transitionActive, string? lastHandledVideoId) =>
        lastHandledVideoId is not null && !isWatchVideo && !transitionActive;
}
