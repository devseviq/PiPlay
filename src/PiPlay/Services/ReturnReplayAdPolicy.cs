namespace PiPlay.Services;

public enum DeferredSeekOutcome
{
    /// <summary>The page reported a clear ad state: the retained seek/rate may be written now.</summary>
    Applied,

    /// <summary>An ad (or unknown state) lasted the whole bounded wait: leave the page's own position and controls.</summary>
    Skipped,

    /// <summary>The replay stopped being current (navigation, identity change, clear, close): write nothing.</summary>
    Abandoned,
}

/// <summary>
/// Deferred return seek (spec 14, YouTube_Compliance, review 2026-09-05 PP-03). A return that lands
/// on an ad may not write <c>currentTime</c> or the playback rate; the request is retained for a
/// bounded wait and applied only when the page reports clear AND the request is still for the
/// video the Source shows. After the bound the native controls are left as they are.
/// </summary>
public static class ReturnReplayAdPolicy
{
    /// <summary>Polls of the ad-state probe before the retained seek is given up.</summary>
    public const int MaxPolls = 12;

    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound of the wait itself, excluding script latency.</summary>
    public static TimeSpan WaitBound => PollInterval * MaxPolls;

    /// <summary>Only a seek or a rate is gated by ad state; play/pause and volume/mute are not.</summary>
    public static bool WantsGuardedWrite(int? seconds, double? playbackRate) =>
        seconds is not null || playbackRate is not null;

    /// <summary>
    /// Poll until clear, until the request is no longer current, or until the bound. The first
    /// read happens before any delay, and currentness is re-checked before every read so a
    /// sample is never replayed onto a video that changed during the wait. Continuations stay on
    /// the caller's context: the callbacks talk to a WebView2 core, which is thread-affine.
    /// </summary>
    public static async Task<DeferredSeekOutcome> WaitForClearAsync(
        Func<Task<YouTubeAdState>> readAdStateAsync,
        Func<bool> isCurrent,
        Func<TimeSpan, Task> delayAsync,
        int maxPolls = MaxPolls)
    {
        ArgumentNullException.ThrowIfNull(readAdStateAsync);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(delayAsync);

        for (var poll = 0; poll < maxPolls; poll++)
        {
            if (poll > 0) await delayAsync(PollInterval);
            if (!isCurrent()) return DeferredSeekOutcome.Abandoned;

            var state = await readAdStateAsync();
            if (!isCurrent()) return DeferredSeekOutcome.Abandoned;
            if (state == YouTubeAdState.Clear) return DeferredSeekOutcome.Applied;
        }

        return DeferredSeekOutcome.Skipped;
    }
}
