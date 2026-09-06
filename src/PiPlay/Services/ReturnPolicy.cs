namespace PiPlay.Services;

/// <summary>What the Source Window should do on return from the Popout Player (spec 14).</summary>
public enum ReturnAction
{
    /// <summary>Do nothing: timestamp unknown and the source was paused at popout.</summary>
    None,
    /// <summary>Resume playback at the current position: timestamp unknown but the source was playing.</summary>
    Play,
    /// <summary>Seek to the last-known timestamp and stay paused.</summary>
    Seek,
    /// <summary>Seek to the last-known timestamp and resume.</summary>
    SeekAndPlay,
    /// <summary>The popout ended on a DIFFERENT video: navigate the source to it (timestamp rides
    /// the watch URL; YouTube's own watch-page playback behavior applies).</summary>
    Navigate,
}

/// <summary>
/// Pure decision for REQ-RETURN-01, extracted from <see cref="MainWindow"/> so it is testable
/// without WebView2. Resume uses the popout's current paused state when known, otherwise the older
/// source-was-playing snapshot. Treat a null <paramref name="lastKnownSeconds"/> as "unknown"
/// (0 is a valid timestamp, not unknown).
/// </summary>
public static class ReturnPolicy
{
    public static ReturnAction Decide(int? lastKnownSeconds, bool sourceWasPlaying)
        => Decide(lastKnownSeconds, sourceWasPlaying, returnedPaused: null);

    public static ReturnAction Decide(int? lastKnownSeconds, bool sourceWasPlaying, bool? returnedPaused)
    {
        var shouldResume = returnedPaused.HasValue ? !returnedPaused.Value : sourceWasPlaying;
        if (lastKnownSeconds is not null)
            return shouldResume ? ReturnAction.SeekAndPlay : ReturnAction.Seek;
        return shouldResume ? ReturnAction.Play : ReturnAction.None;
    }

    /// <summary>
    /// Video-aware overload (overhaul Task 3): when the player reports it ended on a DIFFERENT
    /// video than the one popped out, the source must NAVIGATE there — seeking the original video
    /// to the new video's timestamp is the corruption this fixes. Either id unknown (null/empty)
    /// falls back to the timestamp-only decision above, preserving pre-Task-3 behavior.
    /// </summary>
    public static ReturnAction Decide(
        int? lastKnownSeconds, bool sourceWasPlaying, string? returnedVideoId, string? sourceVideoIdAtPopout)
        => Decide(lastKnownSeconds, sourceWasPlaying, returnedPaused: null, returnedVideoId, sourceVideoIdAtPopout);

    public static ReturnAction Decide(
        int? lastKnownSeconds, bool sourceWasPlaying, bool? returnedPaused,
        string? returnedVideoId, string? sourceVideoIdAtPopout,
        bool popoutLaunchedWithoutVideo = false)
    {
        // Playlist-page launch (spec 13.1 / 22.1): there was no source video id to compare against,
        // so ANY video the popout reports is somewhere the source's playlist page is not. Without
        // this, an empty sourceVideoIdAtPopout would fall into the timestamp branch and strand the
        // user on the playlist page, losing their position in the queue.
        if (popoutLaunchedWithoutVideo && !string.IsNullOrEmpty(returnedVideoId))
            return ReturnAction.Navigate;

        if (!string.IsNullOrEmpty(returnedVideoId) && !string.IsNullOrEmpty(sourceVideoIdAtPopout) &&
            !string.Equals(returnedVideoId, sourceVideoIdAtPopout, StringComparison.Ordinal))
        {
            return ReturnAction.Navigate;
        }
        return Decide(lastKnownSeconds, sourceWasPlaying, returnedPaused);
    }

    /// <summary>
    /// Second look at a decision against the page the Source is ACTUALLY showing (PP-01). The launch
    /// identity is what the Popout was compared against; if the Source has since moved to another
    /// video (a navigation the ownership gate did not stop), a same-video seek would drive the wrong
    /// page. A known returned video navigates there; an unknown one cannot be seeked safely, so
    /// nothing is driven. Unknown live/launch identity keeps the original decision.
    /// </summary>
    public static ReturnAction ReconcileWithLiveSource(
        ReturnAction action, string? returnedVideoId, string? sourceVideoIdAtPopout, string? liveSourceVideoId)
    {
        if (action == ReturnAction.Navigate) return action;
        if (string.IsNullOrEmpty(liveSourceVideoId) || string.IsNullOrEmpty(sourceVideoIdAtPopout)) return action;
        if (string.Equals(liveSourceVideoId, sourceVideoIdAtPopout, StringComparison.Ordinal)) return action;

        return string.IsNullOrEmpty(returnedVideoId) ? ReturnAction.None : ReturnAction.Navigate;
    }

    /// <summary>
    /// Resolve the volume/mute/rate to re-apply to the Source Window on return. The Popout Player's
    /// reported value wins when known; otherwise fall back to the source's pre-suppression launch value.
    /// Mute is forced to a concrete value (default un-muted) because popout launch now MUTES the source
    /// (Q-1 duplicate-audio suppression): leaving it null lets <c>ApplyPlaybackSettingsAsync</c> skip the
    /// mute write, and the source returns silent. This guarantees suppression is always undone on return.
    /// </summary>
    public static (double? Volume, bool? Muted, double? PlaybackRate) ResolveReturnSettings(
        double? popoutVolume, bool? popoutMuted, double? popoutPlaybackRate,
        double? launchVolume, bool? launchMuted, double? launchPlaybackRate)
        => (popoutVolume ?? launchVolume,
            popoutMuted ?? launchMuted ?? false,
            popoutPlaybackRate ?? launchPlaybackRate);
}
