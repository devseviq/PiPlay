using System.Collections.Concurrent;
using System.IO;
using PiPlay.Services;

namespace PiPlay.Tests;

/// <summary>
/// Delivery and shutdown contract of the single-instance hand-off (REQ-APP-01, spec 11, review
/// 2026-09-05 PP-05). Pipe-write success is not delivery; the acknowledgement is.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class SingleInstanceHandoffTests
{
    private static IncomingLinkDecision Decision(IncomingLinkAction action) => new(action, null);

    [Theory]
    [InlineData(HandoffAck.Accepted, "accepted")]
    [InlineData(HandoffAck.Rejected, "rejected")]
    [InlineData(HandoffAck.Unavailable, "unavailable")]
    public void Acknowledgements_round_trip_over_one_wire_line(HandoffAck ack, string wire)
    {
        Assert.Equal(wire, SingleInstanceHandoffPolicy.ToWire(ack));
        Assert.Equal(ack, SingleInstanceHandoffPolicy.ParseAck(wire));
        Assert.Equal(ack, SingleInstanceHandoffPolicy.ParseAck($"  {wire}\r"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok")]
    public void A_missing_or_foreign_line_is_no_acknowledgement(string? line)
    {
        Assert.Null(SingleInstanceHandoffPolicy.ParseAck(line));
        Assert.Equal(HandoffOutcome.NoAcknowledgement,
            SingleInstanceHandoffPolicy.OutcomeFor(SingleInstanceHandoffPolicy.ParseAck(line)));
    }

    [Theory]
    [InlineData(IncomingLinkAction.ActivateOnly, HandoffAck.Accepted)]
    [InlineData(IncomingLinkAction.QueueUntilReady, HandoffAck.Accepted)]
    [InlineData(IncomingLinkAction.NavigateSource, HandoffAck.Accepted)]
    [InlineData(IncomingLinkAction.RetargetPopout, HandoffAck.Accepted)]
    [InlineData(IncomingLinkAction.Retain, HandoffAck.Accepted)]
    [InlineData(IncomingLinkAction.Reject, HandoffAck.Rejected)]
    [InlineData(IncomingLinkAction.Unavailable, HandoffAck.Unavailable)]   // window closing before OnExit
    public void The_answer_follows_the_receiving_decision(IncomingLinkAction action, HandoffAck expected)
    {
        Assert.Equal(expected, SingleInstanceHandoffPolicy.AckFor(Decision(action), shuttingDown: false));
    }

    [Theory]
    [InlineData(IncomingLinkAction.NavigateSource)]
    [InlineData(IncomingLinkAction.Reject)]
    public void Shutdown_answers_unavailable_regardless_of_the_decision(IncomingLinkAction action)
    {
        Assert.Equal(HandoffAck.Unavailable, SingleInstanceHandoffPolicy.AckFor(Decision(action), shuttingDown: true));
    }

    [Theory]
    [InlineData(HandoffOutcome.Accepted, HandoffSenderAction.Exit)]
    [InlineData(HandoffOutcome.Rejected, HandoffSenderAction.ReportLinkRejected)]
    [InlineData(HandoffOutcome.Unavailable, HandoffSenderAction.ElectReplacement)]
    [InlineData(HandoffOutcome.NoAcknowledgement, HandoffSenderAction.ElectReplacement)]
    [InlineData(HandoffOutcome.Unreachable, HandoffSenderAction.ElectReplacement)]
    public void The_sender_never_reports_silent_success_and_never_bypasses_the_mutex(
        HandoffOutcome outcome, HandoffSenderAction expected)
    {
        Assert.Equal(expected, SingleInstanceHandoffPolicy.DecideSenderAction(outcome));
    }

    [Theory]
    [InlineData(HandoffOutcome.Unreachable, 1, true)]
    [InlineData(HandoffOutcome.NoAcknowledgement, 1, true)]
    [InlineData(HandoffOutcome.Unreachable, 2, false)]      // attempts are bounded
    [InlineData(HandoffOutcome.Unavailable, 1, false)]      // an answer is final
    [InlineData(HandoffOutcome.Rejected, 1, false)]
    [InlineData(HandoffOutcome.Accepted, 1, false)]
    public void Only_transport_failures_are_retried_and_only_briefly(HandoffOutcome outcome, int attempt, bool retry)
    {
        Assert.Equal(retry, SingleInstanceHandoffPolicy.ShouldRetry(outcome, attempt));
    }

    [Fact]
    public async Task Send_returns_the_acknowledged_outcome_without_retrying()
    {
        var attempts = 0;
        var outcome = await SingleInstanceHandoffPolicy.SendAsync(
            (_, _) => { attempts++; return Task.FromResult<string?>("accepted"); },
            (_, _) => throw new InvalidOperationException("no delay expected"),
            (_, _) => throw new InvalidOperationException("no failure expected"),
            CancellationToken.None);

        Assert.Equal(HandoffOutcome.Accepted, outcome);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Send_retries_an_unreachable_server_once_then_reports_it()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var failures = new List<int>();

        var outcome = await SingleInstanceHandoffPolicy.SendAsync(
            (_, _) => { attempts++; throw new IOException("no pipe"); },
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
            (attempt, _) => failures.Add(attempt),
            CancellationToken.None);

        Assert.Equal(HandoffOutcome.Unreachable, outcome);
        Assert.Equal(SingleInstanceHandoffPolicy.MaxSendAttempts, attempts);
        Assert.Equal(new[] { 1, 2 }, failures);
        Assert.Equal(new[] { SingleInstanceHandoffPolicy.RetryDelay }, delays);
    }

    [Fact]
    public async Task Send_treats_a_silent_server_as_no_acknowledgement_and_a_late_answer_wins()
    {
        var answers = new Queue<Func<string?>>(new Func<string?>[]
        {
            () => throw new TimeoutException("no ack"),
            () => "unavailable",
        });

        var outcome = await SingleInstanceHandoffPolicy.SendAsync(
            (_, _) => Task.FromResult(answers.Dequeue()()),
            (_, _) => Task.CompletedTask,
            (_, _) => { },
            CancellationToken.None);

        Assert.Equal(HandoffOutcome.Unavailable, outcome);
        Assert.Empty(answers);
    }

    [Fact]
    public async Task Send_gives_up_when_the_server_closes_without_answering_twice()
    {
        var outcome = await SingleInstanceHandoffPolicy.SendAsync(
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask,
            (_, _) => { },
            CancellationToken.None);

        Assert.Equal(HandoffOutcome.NoAcknowledgement, outcome);
    }

    [Fact]
    public void A_dispatch_generation_is_current_until_it_is_expired()
    {
        var generation = 0;
        var begun = SingleInstanceHandoffPolicy.BeginDispatch(ref generation);
        Assert.True(SingleInstanceHandoffPolicy.IsCurrentDispatch(begun, generation));

        SingleInstanceHandoffPolicy.ExpireDispatch(ref generation);
        Assert.False(SingleInstanceHandoffPolicy.IsCurrentDispatch(begun, generation));
    }

    [Fact]
    public void A_second_dispatch_start_invalidates_the_first_generation()
    {
        var generation = 0;
        var first = SingleInstanceHandoffPolicy.BeginDispatch(ref generation);
        var second = SingleInstanceHandoffPolicy.BeginDispatch(ref generation);

        Assert.NotEqual(first, second);
        Assert.False(SingleInstanceHandoffPolicy.IsCurrentDispatch(first, generation));
        Assert.True(SingleInstanceHandoffPolicy.IsCurrentDispatch(second, generation));
    }

    [Fact]
    public void Concurrent_dispatch_starts_never_share_a_generation()
    {
        // The pipe worker begins a dispatch while a timer continuation expires the previous one,
        // so a lost increment would hand two dispatches the same generation.
        var generation = 0;
        var begun = new ConcurrentBag<int>();
        Parallel.For(0, 20_000, _ => begun.Add(SingleInstanceHandoffPolicy.BeginDispatch(ref generation)));

        Assert.Equal(20_000, begun.Distinct().Count());
        Assert.Equal(20_000, generation);
    }

    [Fact]
    public async Task A_dispatch_wait_that_times_out_expires_the_operation_and_answers_unavailable()
    {
        var expired = false;
        var dispatched = new TaskCompletionSource<HandoffAck>(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = SingleInstanceHandoffPolicy.AwaitDispatchAsync(
            dispatched.Task,
            () => expired = true,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(30));

        var ack = await wait;
        dispatched.TrySetResult(HandoffAck.Accepted);

        Assert.Equal(HandoffAck.Unavailable, ack);
        Assert.True(expired);
    }

    [Fact]
    public async Task A_dispatch_that_answers_in_time_is_not_expired()
    {
        var expired = false;
        var dispatched = new TaskCompletionSource<HandoffAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatched.SetResult(HandoffAck.Accepted);

        var ack = await SingleInstanceHandoffPolicy.AwaitDispatchAsync(
            dispatched.Task,
            () => expired = true,
            CancellationToken.None,
            TimeSpan.FromSeconds(1));

        Assert.Equal(HandoffAck.Accepted, ack);
        Assert.False(expired);
    }

    [Fact]
    public async Task A_cancelled_dispatch_wait_rethrows_and_never_expires_the_operation()
    {
        var expired = false;
        var dispatched = new TaskCompletionSource<HandoffAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SingleInstanceHandoffPolicy.AwaitDispatchAsync(
            dispatched.Task,
            () => expired = true,
            cts.Token,
            TimeSpan.FromSeconds(1)));

        // The dispatcher operation was queued with the same token and is aborted by it; the wait
        // has nothing to expire, and the caller must see the cancellation rather than Unavailable.
        Assert.False(expired);
    }

    [Fact]
    public async Task Send_propagates_the_callers_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SingleInstanceHandoffPolicy.SendAsync(
            (_, token) => Task.FromCanceled<string?>(token),
            (_, _) => Task.CompletedTask,
            (_, _) => { },
            cts.Token));
    }

    [Fact]
    public void Shutdown_stops_accepting_then_waits_then_drains_then_releases_and_never_skips_a_step()
    {
        var order = new List<string>();
        var failed = new List<string>();

        SingleInstanceHandoffPolicy.RunShutdownSequence(
            () => order.Add("stop"),
            () => { order.Add("wait"); throw new TimeoutException("worker still dispatching"); },
            () => order.Add("drain"),
            () => order.Add("release"),
            (step, _) => failed.Add(step));

        Assert.Equal(new[] { "stop", "wait", "drain", "release" }, order);
        Assert.Equal(new[] { "wait-worker" }, failed);
    }

    [Fact]
    public void A_closing_window_maps_to_a_replacement_election_end_to_end()
    {
        var decision = IncomingLinkPolicy.Decide("https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            new IncomingLinkState(true, false, false, false, false, Closing: true));
        var ack = SingleInstanceHandoffPolicy.AckFor(decision, shuttingDown: false);
        var outcome = SingleInstanceHandoffPolicy.OutcomeFor(SingleInstanceHandoffPolicy.ParseAck(SingleInstanceHandoffPolicy.ToWire(ack)));

        Assert.Equal(HandoffSenderAction.ElectReplacement, SingleInstanceHandoffPolicy.DecideSenderAction(outcome));
    }

    // --- the real wire: one duplex pipe, line framing, ack delivery ---

    private static string LoopbackPipeName() => $"PiPlay.Tests.Handoff.{Guid.NewGuid():N}";

    private static Task ServeAsync(string pipe, HandoffAck ack, List<string> received, CancellationToken token) =>
        SingleInstancePipeTransport.ServeOneAsync(
            pipe,
            (payload, _) => { received.Add(payload); return Task.FromResult(ack); },
            (_, ex) => throw new InvalidOperationException("ack should be deliverable", ex),
            ex => throw new InvalidOperationException("request should be usable", ex),
            token);

    private static readonly TimeSpan ShortBound = TimeSpan.FromMilliseconds(200);

    // A bound a real sender's payload must always beat, even on a loaded CI runner: a short bound
    // is only for the misbehaviour a test waits out, never for a write the test expects to land.
    private static readonly TimeSpan PayloadBound = TimeSpan.FromSeconds(1);

    /// <summary>One connection with a short answer bound so a sender that never reads times out fast.</summary>
    private static Task ServeBrieflyAsync(
        string pipe, List<string> received, List<Exception> unusable, List<HandoffAck> undeliverable,
        TimeSpan payloadTimeout, CancellationToken token) =>
        SingleInstancePipeTransport.ServeOneAsync(
            pipe,
            (payload, _) => { lock (received) received.Add(payload); return Task.FromResult(HandoffAck.Accepted); },
            (ack, _) => { lock (undeliverable) undeliverable.Add(ack); },
            ex => { lock (unusable) unusable.Add(ex); },
            payloadTimeout, ShortBound, token);

    [Fact]
    public async Task The_acknowledgement_round_trips_over_a_real_pipe()
    {
        var pipe = LoopbackPipeName();
        var received = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAsync(pipe, HandoffAck.Accepted, received, cts.Token);

        var line = await SingleInstancePipeTransport.ExchangeAsync(pipe, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", cts.Token);
        await server;

        Assert.Equal("accepted", line);
        Assert.Equal(new[] { "https://www.youtube.com/watch?v=dQw4w9WgXcQ" }, received);
        Assert.Equal(HandoffOutcome.Accepted, SingleInstanceHandoffPolicy.OutcomeFor(SingleInstanceHandoffPolicy.ParseAck(line)));
    }

    [Fact]
    public async Task A_legacy_client_that_writes_without_a_newline_and_closes_is_still_served()
    {
        var pipe = LoopbackPipeName();
        var received = new List<string>();
        var undeliverable = new List<HandoffAck>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = SingleInstancePipeTransport.ServeOneAsync(
            pipe,
            (payload, _) => { received.Add(payload); return Task.FromResult(HandoffAck.Accepted); },
            (ack, _) => undeliverable.Add(ack),
            ex => throw new InvalidOperationException("request should be usable", ex),
            cts.Token);

        using (var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.Out))
        {
            await client.ConnectAsync(2000, cts.Token);
            var bytes = System.Text.Encoding.UTF8.GetBytes("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
            await client.WriteAsync(bytes, cts.Token);
        }

        await server;

        Assert.Equal(new[] { "https://www.youtube.com/watch?v=dQw4w9WgXcQ" }, received);
        // The old client never reads; the server must not fault on the undeliverable answer.
        Assert.True(undeliverable.Count <= 1);
    }

    [Fact]
    public async Task A_server_that_closes_without_answering_is_no_acknowledgement_not_a_crash()
    {
        var pipe = LoopbackPipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var s = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            await s.WaitForConnectionAsync(cts.Token);
            using var reader = new StreamReader(s, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true);
            _ = await reader.ReadLineAsync(cts.Token);   // take the request, then vanish without an answer
        });

        var line = await SingleInstancePipeTransport.ExchangeAsync(pipe, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", cts.Token);
        await server;

        Assert.Null(line);
        Assert.Equal(HandoffOutcome.NoAcknowledgement, SingleInstanceHandoffPolicy.OutcomeFor(SingleInstanceHandoffPolicy.ParseAck(line)));
    }

    [Fact]
    public async Task No_server_pipe_is_unreachable_not_silence()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() =>
            SingleInstancePipeTransport.ExchangeAsync(LoopbackPipeName(), "x", cts.Token));
    }

    // --- client misbehaviour is the connection's problem, never the server's (review 2026-09-24) ---

    [Fact]
    public async Task A_client_that_connects_and_never_writes_is_dropped_without_failing_the_server()
    {
        var pipe = LoopbackPipeName();
        var received = new List<string>();
        var unusable = new List<Exception>();
        var undeliverable = new List<HandoffAck>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeBrieflyAsync(pipe, received, unusable, undeliverable, ShortBound, cts.Token);

        using var silent = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut);
        await silent.ConnectAsync(2000, cts.Token);

        // Returns normally: a thrown TimeoutException here is what used to push the listener into
        // retry backoff (up to 30 s) for every silent connection.
        await server.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(received);
        Assert.IsType<TimeoutException>(Assert.Single(unusable));
        Assert.Empty(undeliverable);
    }

    [Fact]
    public async Task A_silent_client_costs_the_listener_no_backoff_and_the_next_sender_is_served()
    {
        var pipe = LoopbackPipeName();
        var received = new List<string>();
        var dropped = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<Exception>();
        var delays = new List<TimeSpan>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = SingleInstancePipePolicy.RunAsync(
            attemptAsync: token => SingleInstancePipeTransport.ServeOneAsync(
                pipe,
                (payload, _) => { lock (received) received.Add(payload); return Task.FromResult(HandoffAck.Accepted); },
                (_, ex) => throw new InvalidOperationException("ack should be deliverable", ex),
                ex => dropped.TrySetResult(ex),
                PayloadBound, ShortBound, token),
            delayAsync: (delay, token) => { lock (delays) delays.Add(delay); return Task.Delay(delay, token); },
            onFirstFailure: ex => { lock (failures) failures.Add(ex); },
            onRecovery: _ => { },
            cts.Token);

        using (var silent = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut))
        {
            await silent.ConnectAsync(2000, cts.Token);
            Assert.IsType<TimeoutException>(await dropped.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            // The listener went straight back to accepting: the next sender is answered at once.
            var line = await SingleInstancePipeTransport.ExchangeAsync(pipe, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", cts.Token);
            Assert.Equal("accepted", line);
        }

        cts.Cancel();
        await listener.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "https://www.youtube.com/watch?v=dQw4w9WgXcQ" }, received);
        Assert.Empty(failures);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task An_overlong_payload_is_answered_rejected_and_never_dispatched()
    {
        var pipe = LoopbackPipeName();
        var received = new List<string>();
        var unusable = new List<Exception>();
        var undeliverable = new List<HandoffAck>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = SingleInstancePipeTransport.ServeOneAsync(
            pipe,
            (payload, _) => { received.Add(payload); return Task.FromResult(HandoffAck.Accepted); },
            (ack, _) => undeliverable.Add(ack),
            unusable.Add,
            cts.Token);

        // A valid link followed by padding: a truncated prefix would still parse, so nothing of an
        // overlong line may reach the receiving decision.
        var overlong = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&pad=" +
            new string('a', SingleInstancePipePolicy.MaxPayloadBytes);
        var line = await SingleInstancePipeTransport.ExchangeAsync(pipe, overlong, cts.Token);
        await server;

        Assert.Equal("rejected", line);
        Assert.Empty(received);
        Assert.IsType<InvalidDataException>(Assert.Single(unusable));
        Assert.Empty(undeliverable);
    }

    [Fact]
    public async Task A_sender_that_never_reads_or_hangs_up_cannot_stall_the_server()
    {
        var pipe = LoopbackPipeName();
        var received = new List<string>();
        var unusable = new List<Exception>();
        var undeliverable = new List<HandoffAck>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeBrieflyAsync(pipe, received, unusable, undeliverable, PayloadBound, cts.Token);

        using var mute = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut);
        await mute.ConnectAsync(2000, cts.Token);
        await mute.WriteAsync(System.Text.Encoding.UTF8.GetBytes("https://www.youtube.com/watch?v=dQw4w9WgXcQ\n"), cts.Token);

        // The blocking WaitForPipeDrain this replaced never returned for a sender that stays
        // connected without reading; the bounded wait lets the worker move on.
        await server.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "https://www.youtube.com/watch?v=dQw4w9WgXcQ" }, received);
        Assert.Empty(unusable);
        Assert.True(undeliverable.Count <= 1);   // a zero-quota pipe may time the write out instead
    }

    [Fact]
    public async Task The_payload_line_is_bounded_and_framed_by_newline_or_eof()
    {
        static Task<string?> Read(byte[] bytes) =>
            SingleInstancePipeTransport.ReadPayloadLineAsync(new MemoryStream(bytes), CancellationToken.None);
        static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

        var atLimit = new string('a', SingleInstancePipePolicy.MaxPayloadBytes);
        Assert.Equal(atLimit, await Read(Bytes(atLimit + "\n")));
        Assert.Equal(atLimit, await Read(Bytes(atLimit + "\r\n")));           // the CR is framing, not payload
        Assert.Equal(atLimit, await Read(Bytes(atLimit)));                  // EOF closes a legacy line
        Assert.Null(await Read(Bytes(atLimit + "a\n")));
        Assert.Null(await Read(Bytes(atLimit + "a")));
        Assert.Equal("https://youtu.be/dQw4w9WgXcQ", await Read(Bytes("https://youtu.be/dQw4w9WgXcQ\r\nextra")));
        Assert.Equal(string.Empty, await Read(Array.Empty<byte>()));         // no link only activates
        Assert.Equal("é", await Read(Bytes("é\n")));
    }

    [Fact]
    public void Handoff_deadlines_are_bounded_and_shorter_than_a_user_would_wait()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), SingleInstanceHandoffPolicy.AckTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), SingleInstanceHandoffPolicy.DispatchTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), SingleInstanceHandoffPolicy.ReplacementElectionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), SingleInstanceHandoffPolicy.WorkerShutdownWait);
        Assert.True(SingleInstanceHandoffPolicy.DispatchTimeout > SingleInstanceHandoffPolicy.AckTimeout,
            "a dispatch that outlives the sender's patience still answers the next sender truthfully");
    }
}
