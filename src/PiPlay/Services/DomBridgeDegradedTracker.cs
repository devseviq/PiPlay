namespace PiPlay.Services;

/// <summary>
/// Decides when a DOM bridge failure episode becomes UI-visible and when the whole bridge is
/// healthy again (the final-review follow-up to readiness A-1). Degraded state is keyed by
/// (surface, operation) because the failure gates live per-WebView: the Source and a popout run
/// the same operation names on different surfaces, and a healthy read on one must neither clear
/// the other's hint mid-episode nor hide it while other operations are still degraded. Pure
/// bookkeeping over counts so it is unit-testable without a WebView2; the bridge stays thin.
/// </summary>
internal sealed class DomBridgeDegradedTracker
{
    /// <summary>Consecutive failures after which an operation counts as degraded.</summary>
    public const int FailureThreshold = 3;

    private readonly object _sync = new();
    private readonly HashSet<string> _degraded = new(StringComparer.Ordinal);

    /// <summary>
    /// Record that (surface, operation) just reached this many consecutive failures. Returns the
    /// operation name when this failure newly crosses the threshold - the one raise per key per
    /// episode - and null when there is nothing to raise.
    /// </summary>
    public string? RecordFailure(Guid surfaceId, string operation, int failureCount)
    {
        if (failureCount < FailureThreshold) return null;
        lock (_sync)
        {
            if (!_degraded.Add(Key(surfaceId, operation))) return null;
        }
        return operation;
    }

    /// <summary>
    /// Record that (surface, operation) executed successfully. Returns true only when that key was
    /// degraded and no degraded key remains anywhere - the one condition that clears the UI hint.
    /// </summary>
    public bool RecordRecovery(Guid surfaceId, string operation)
    {
        lock (_sync)
        {
            if (!_degraded.Remove(Key(surfaceId, operation))) return false;
            return _degraded.Count == 0;
        }
    }

    private static string Key(Guid surfaceId, string operation) => $"{surfaceId}|{operation}";
}
