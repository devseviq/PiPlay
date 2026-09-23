using PiPlay.Models;

namespace PiPlay.Services;

/// <summary>
/// Host-side writers used by <see cref="ReturnPlaybackApply"/>. Volume/mute and play/pause are
/// not ad-gated; seek and rate are.
/// </summary>
public readonly record struct ReturnPlaybackWriters(
    Func<double?, bool?, double?, Task> ApplySettingsAsync,
    Func<bool?, Task> ApplyPlayPauseAsync,
    Func<int, Task> SeekAsync);

/// <summary>
/// One apply path for every return (spec 14): volume/mute and play/pause go immediately; seek
/// and rate wait for a clear page and are not written if the request stops being current during
/// an await.
/// </summary>
public static class ReturnPlaybackApply
{
    public static async Task<DeferredSeekOutcome> ApplyAsync(
        PlayerReturnState state,
        Func<Task<YouTubeAdState>> readAdStateAsync,
        ReturnPlaybackWriters writers,
        Func<bool> isCurrent,
        Func<TimeSpan, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(readAdStateAsync);
        ArgumentNullException.ThrowIfNull(writers.ApplySettingsAsync);
        ArgumentNullException.ThrowIfNull(writers.ApplyPlayPauseAsync);
        ArgumentNullException.ThrowIfNull(writers.SeekAsync);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(delayAsync);

        if (!isCurrent()) return DeferredSeekOutcome.Abandoned;

        await writers.ApplySettingsAsync(state.Volume, state.Muted, null);
        if (!isCurrent()) return DeferredSeekOutcome.Abandoned;

        await writers.ApplyPlayPauseAsync(state.Paused);
        if (!isCurrent()) return DeferredSeekOutcome.Abandoned;

        if (!ReturnReplayAdPolicy.WantsGuardedWrite(state.LastKnownSeconds, state.PlaybackRate))
            return DeferredSeekOutcome.Applied;

        var wait = await ReturnReplayAdPolicy.WaitForClearAsync(readAdStateAsync, isCurrent, delayAsync);
        if (wait != DeferredSeekOutcome.Applied) return wait;
        if (!isCurrent()) return DeferredSeekOutcome.Abandoned;

        if (state.LastKnownSeconds is { } seconds)
        {
            await writers.SeekAsync(seconds);
            if (!isCurrent()) return DeferredSeekOutcome.Abandoned;
        }

        if (state.PlaybackRate is not null)
        {
            await writers.ApplySettingsAsync(null, null, state.PlaybackRate);
            if (!isCurrent()) return DeferredSeekOutcome.Abandoned;
        }

        return DeferredSeekOutcome.Applied;
    }
}
