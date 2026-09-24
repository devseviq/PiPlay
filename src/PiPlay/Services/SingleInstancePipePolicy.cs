namespace PiPlay.Services;

/// <summary>Identity and failure policy for the single-instance named-pipe server.</summary>
internal static class SingleInstancePipePolicy
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ClientReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Longest payload line the server keeps; a supported link is far shorter.</summary>
    public const int MaxPayloadBytes = 8 * 1024;

    /// <summary>
    /// Match the <c>Local\</c> mutex boundary: each logon session gets its own primary and pipe,
    /// while repeated launches inside that session still rendezvous on one stable name.
    /// </summary>
    public static string BuildPipeName(string identitySuffix, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identitySuffix);
        if (sessionId < 0) throw new ArgumentOutOfRangeException(nameof(sessionId));
        return $"PiPlay.SingleInstance.{identitySuffix}.Session{sessionId}";
    }

    public static TimeSpan RetryDelay(int consecutiveFailureCount)
    {
        if (consecutiveFailureCount <= 1) return InitialRetryDelay;

        // 250 ms, 500 ms, 1 s, 2 s, 4 s, 8 s, 16 s, then the 30 s cap. Clamp the
        // exponent before shifting so a long-lived failure cannot overflow.
        var exponent = Math.Min(consecutiveFailureCount - 1, 7);
        var milliseconds = InitialRetryDelay.TotalMilliseconds * (1 << exponent);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaximumRetryDelay.TotalMilliseconds));
    }

    public static Task<T> ReadClientPayloadAsync<T>(
        Func<CancellationToken, Task<T>> readAsync,
        CancellationToken cancellationToken) =>
        ReadClientPayloadAsync(readAsync, ClientReadTimeout, cancellationToken);

    internal static Task<T> ReadClientPayloadAsync<T>(
        Func<CancellationToken, Task<T>> readAsync,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        AsyncOperationDeadline.RunAsync(readAsync, timeout, cancellationToken);

    /// <summary>
    /// Serve connections until shutdown. <paramref name="attemptAsync"/> serves one connection and
    /// throws only for a server-side failure (the pipe could not be created or accept): a client
    /// that stays silent or misbehaves is the attempt's own business and must not reach here, or
    /// any local client could hold the listener in backoff.
    /// </summary>
    public static async Task RunAsync(
        Func<CancellationToken, Task> attemptAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action<Exception> onFirstFailure,
        Action<int> onRecovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attemptAsync);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentNullException.ThrowIfNull(onFirstFailure);
        ArgumentNullException.ThrowIfNull(onRecovery);

        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await attemptAsync(cancellationToken).ConfigureAwait(false);
                if (consecutiveFailures > 0)
                    onRecovery(consecutiveFailures);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) return;

                if (consecutiveFailures < int.MaxValue)
                    consecutiveFailures++;
                if (consecutiveFailures == 1)
                    onFirstFailure(ex);

                try
                {
                    await delayAsync(RetryDelay(consecutiveFailures), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
