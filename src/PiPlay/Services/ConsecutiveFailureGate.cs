namespace PiPlay.Services;

/// <summary>
/// Coalesces one consecutive failure episode into its first error plus a recovery summary.
/// Thread-safe because WebView continuations and the pipe loop are not required to share a caller.
/// </summary>
internal sealed class ConsecutiveFailureGate
{
    private readonly object _sync = new();
    private int _failureCount;

    /// <summary>Record one failure. Returns true only for the first failure in the episode.</summary>
    public bool RecordFailure() => RecordFailureCount() == 1;

    /// <summary>Record one failure and return the episode's current consecutive-failure count.</summary>
    public int RecordFailureCount()
    {
        lock (_sync)
        {
            if (_failureCount < int.MaxValue)
                _failureCount++;
            return _failureCount;
        }
    }

    /// <summary>
    /// End the current episode and return the number of repeat failures suppressed after the first.
    /// A success while healthy returns null; zero therefore means one failure followed by recovery.
    /// </summary>
    public int? RecordSuccess()
    {
        lock (_sync)
        {
            if (_failureCount == 0) return null;
            var suppressed = Math.Max(0, _failureCount - 1);
            _failureCount = 0;
            return suppressed;
        }
    }
}
