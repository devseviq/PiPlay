namespace PiPlay.Services;

/// <summary>What the running instance tells a second launch about its request (one wire line).</summary>
public enum HandoffAck
{
    /// <summary>A live owner took the request (navigated, retargeted, retained, or just activated).</summary>
    Accepted,

    /// <summary>The instance is alive and came forward, but the payload was not a usable link.</summary>
    Rejected,

    /// <summary>The instance is shutting down or did not answer within its dispatch bound.</summary>
    Unavailable,
}

/// <summary>The sender's view of one hand-off exchange, including transport failures.</summary>
public enum HandoffOutcome
{
    Accepted,
    Rejected,
    Unavailable,

    /// <summary>The pipe connected and the payload went out, but no acknowledgement line came back in time.</summary>
    NoAcknowledgement,

    /// <summary>No server pipe answered the connection.</summary>
    Unreachable,
}

/// <summary>What the second launch does after the exchange settled.</summary>
public enum HandoffSenderAction
{
    /// <summary>The running instance owns the request: exit quietly.</summary>
    Exit,

    /// <summary>The running instance is up but refused the link: say so, then exit.</summary>
    ReportLinkRejected,

    /// <summary>
    /// Nobody answered for the request: contend for the session mutex briefly and become the
    /// primary only after winning it; otherwise report the unresponsive instance and exit.
    /// </summary>
    ElectReplacement,
}

/// <summary>
/// Delivery contract for the single-instance hand-off (REQ-APP-01, spec 11, PP-05). Pipe-write
/// success is not delivery: the running instance answers with an acknowledgement line after its
/// UI thread applied the request, and the sender's next step depends on that answer, never on a
/// bypassed mutex.
/// </summary>
public static class SingleInstanceHandoffPolicy
{
    /// <summary>How long the sender waits for the acknowledgement after the payload was written.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long the server waits for its UI thread to apply one request before answering Unavailable.</summary>
    public static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a sender that got no owner waits to win the session mutex before giving up visibly.</summary>
    public static readonly TimeSpan ReplacementElectionTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long shutdown waits for the pipe worker after cancelling it.</summary>
    public static readonly TimeSpan WorkerShutdownWait = TimeSpan.FromSeconds(2);

    public const int MaxSendAttempts = 2;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

    public static string ToWire(HandoffAck ack) => ack switch
    {
        HandoffAck.Accepted => "accepted",
        HandoffAck.Rejected => "rejected",
        _ => "unavailable",
    };

    /// <summary>Null for a missing or malformed line: the sender treats that as no acknowledgement.</summary>
    public static HandoffAck? ParseAck(string? line) => line?.Trim() switch
    {
        "accepted" => HandoffAck.Accepted,
        "rejected" => HandoffAck.Rejected,
        "unavailable" => HandoffAck.Unavailable,
        _ => null,
    };

    /// <summary>The server's answer for a decision the Source Window made (or could not make).</summary>
    public static HandoffAck AckFor(IncomingLinkDecision decision, bool shuttingDown)
    {
        if (shuttingDown) return HandoffAck.Unavailable;
        return decision.Action switch
        {
            IncomingLinkAction.Reject => HandoffAck.Rejected,
            // The window flagged itself closing before App.OnExit set the process-wide flag; the
            // sender must elect a replacement, not report a bad link.
            IncomingLinkAction.Unavailable => HandoffAck.Unavailable,
            _ => HandoffAck.Accepted,
        };
    }

    public static HandoffOutcome OutcomeFor(HandoffAck? ack) => ack switch
    {
        HandoffAck.Accepted => HandoffOutcome.Accepted,
        HandoffAck.Rejected => HandoffOutcome.Rejected,
        HandoffAck.Unavailable => HandoffOutcome.Unavailable,
        _ => HandoffOutcome.NoAcknowledgement,
    };

    /// <summary>Transport-level failures are retried a bounded number of times; answers are final.</summary>
    public static bool ShouldRetry(HandoffOutcome outcome, int attempt, int maxAttempts = MaxSendAttempts) =>
        attempt < maxAttempts && outcome is HandoffOutcome.Unreachable or HandoffOutcome.NoAcknowledgement;

    public static HandoffSenderAction DecideSenderAction(HandoffOutcome outcome) => outcome switch
    {
        HandoffOutcome.Accepted => HandoffSenderAction.Exit,
        HandoffOutcome.Rejected => HandoffSenderAction.ReportLinkRejected,
        _ => HandoffSenderAction.ElectReplacement,
    };

    /// <summary>
    /// Start a new UI dispatch. The returned generation is current until <see cref="ExpireDispatch"/>
    /// or the next <see cref="BeginDispatch"/>. Atomic: the pipe worker starts a dispatch while a
    /// timer continuation may be expiring the previous one.
    /// </summary>
    public static int BeginDispatch(ref int generation) => Interlocked.Increment(ref generation);

    public static bool IsCurrentDispatch(int begunGeneration, int currentGeneration) =>
        begunGeneration == currentGeneration;

    /// <summary>
    /// Invalidate an in-flight dispatch so a late UI callback cannot apply a request that already
    /// answered Unavailable. Atomic for the same reason as <see cref="BeginDispatch"/>; the UI
    /// thread must read the field with <c>Volatile.Read</c> to see the write.
    /// </summary>
    public static void ExpireDispatch(ref int generation) => Interlocked.Increment(ref generation);

    /// <summary>
    /// Wait for the UI thread to apply one hand-off. A timeout or dispatcher abort answers
    /// Unavailable and runs <paramref name="onExpired"/> so the still-queued callback is cancelled.
    /// Cancellation is the caller's to handle and is rethrown without expiring anything. The wait
    /// never resumes on a captured context: the caller is the pipe worker, not the UI thread.
    /// </summary>
    public static async Task<HandoffAck> AwaitDispatchAsync(
        Task<HandoffAck> dispatched,
        Action onExpired,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(dispatched);
        ArgumentNullException.ThrowIfNull(onExpired);
        try
        {
            return await dispatched.WaitAsync(timeout ?? DispatchTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            onExpired();
            return HandoffAck.Unavailable;
        }
    }

    /// <summary>
    /// Run the exchange with retries. <paramref name="exchangeAsync"/> connects, writes the payload,
    /// and returns the acknowledgement line (null when the server closed without one); it throws
    /// <see cref="TimeoutException"/> when the line did not arrive in time and any other exception
    /// when no server answered.
    /// </summary>
    public static async Task<HandoffOutcome> SendAsync(
        Func<int, CancellationToken, Task<string?>> exchangeAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action<int, Exception> onAttemptFailed,
        CancellationToken cancellationToken,
        int maxAttempts = MaxSendAttempts)
    {
        ArgumentNullException.ThrowIfNull(exchangeAsync);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentNullException.ThrowIfNull(onAttemptFailed);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        var outcome = HandoffOutcome.Unreachable;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var line = await exchangeAsync(attempt, cancellationToken).ConfigureAwait(false);
                outcome = OutcomeFor(ParseAck(line));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                outcome = HandoffOutcome.NoAcknowledgement;
                onAttemptFailed(attempt, ex);
            }
            catch (Exception ex)
            {
                outcome = HandoffOutcome.Unreachable;
                onAttemptFailed(attempt, ex);
            }

            if (!ShouldRetry(outcome, attempt, maxAttempts)) return outcome;
            await delayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>
    /// Shutdown ownership order (PP-05): stop taking requests, let the worker finish (bounded), drain
    /// the log, and only then give the session mutex up so a replacement never starts against a
    /// primary that is still writing. Every step is best-effort; a failing step never skips the rest.
    /// </summary>
    public static void RunShutdownSequence(
        Action stopAcceptingRequests,
        Action waitForWorker,
        Action drainLog,
        Action releaseMutex,
        Action<string, Exception> onStepFailed)
    {
        ArgumentNullException.ThrowIfNull(onStepFailed);
        Step("stop-accepting", stopAcceptingRequests);
        Step("wait-worker", waitForWorker);
        Step("drain-log", drainLog);
        Step("release-mutex", releaseMutex);

        void Step(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { onStepFailed(name, ex); }
        }
    }
}
