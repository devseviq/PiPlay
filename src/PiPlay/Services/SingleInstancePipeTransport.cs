using System.IO;
using System.IO.Pipes;
using System.Text;

namespace PiPlay.Services;

/// <summary>
/// The wire of the single-instance hand-off (spec 11, PP-05): one line in, one line back, over one
/// duplex named pipe per request. Kept free of App state so the real framing runs in the test lane.
/// Both ends are <see cref="PipeOptions.CurrentUserOnly"/>: the server admits only this user, and the
/// sender refuses a pipe another user created under the same name.
/// </summary>
internal static class SingleInstancePipeTransport
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    /// <summary>
    /// Accept one connection, read the payload line (bounded by the client read timeout and
    /// <see cref="SingleInstancePipePolicy.MaxPayloadBytes"/>; a legacy client that writes without a
    /// newline and closes still yields its text at EOF), dispatch it, and answer with the
    /// acknowledgement line. A sender that stays silent past the bound, or whose read fails with an
    /// I/O error, is dropped unanswered; one that hangs up before a newline is served its text up to
    /// that point as a legacy line (an empty line only activates), and the answer it can no longer
    /// read is reported undeliverable. An overlong line is answered Rejected without being
    /// dispatched. Every case returns normally, because a misbehaving client is not a server
    /// failure and must not push the listener into retry backoff.
    /// </summary>
    public static Task ServeOneAsync(
        string pipeName,
        Func<string, CancellationToken, Task<HandoffAck>> dispatchAsync,
        Action<HandoffAck, Exception> onAckUndeliverable,
        Action<Exception> onRequestUnusable,
        CancellationToken token) =>
        ServeOneAsync(
            pipeName, dispatchAsync, onAckUndeliverable, onRequestUnusable,
            SingleInstancePipePolicy.ClientReadTimeout, SingleInstanceHandoffPolicy.AckTimeout, token);

    internal static async Task ServeOneAsync(
        string pipeName,
        Func<string, CancellationToken, Task<HandoffAck>> dispatchAsync,
        Action<HandoffAck, Exception> onAckUndeliverable,
        Action<Exception> onRequestUnusable,
        TimeSpan payloadTimeout,
        TimeSpan answerTimeout,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(dispatchAsync);
        ArgumentNullException.ThrowIfNull(onAckUndeliverable);
        ArgumentNullException.ThrowIfNull(onRequestUnusable);

        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, Options);

        await server.WaitForConnectionAsync(token).ConfigureAwait(false);

        string? payload;
        try
        {
            payload = await SingleInstancePipePolicy.ReadClientPayloadAsync(
                ct => ReadPayloadLineAsync(server, ct), payloadTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            onRequestUnusable(ex);
            return;
        }

        HandoffAck ack;
        if (payload is null)
        {
            // Never handed to the UI, not even in part: a truncated prefix could still parse as a
            // different target.
            onRequestUnusable(new InvalidDataException(
                $"The hand-off payload exceeded {SingleInstancePipePolicy.MaxPayloadBytes} bytes."));
            ack = HandoffAck.Rejected;
        }
        else
        {
            ack = await dispatchAsync(payload, token).ConfigureAwait(false);
        }

        try
        {
            // Raw bytes, no StreamWriter: its Dispose flushes and throws "Pipe is broken" when the
            // sender already left, which would turn a served request into a server failure.
            await AsyncOperationDeadline.RunAsync(
                async ct =>
                {
                    await WriteLineAsync(server, SingleInstanceHandoffPolicy.ToWire(ack), ct).ConfigureAwait(false);
                    return true;
                },
                answerTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            onAckUndeliverable(ack, ex);
            return;
        }

        try
        {
            // Close only after the sender hangs up (it does right after reading the answer), so the
            // answer is never discarded with the pipe. Asynchronous and bounded, unlike the blocking
            // WaitForPipeDrain: a sender that never reads costs this connection, not the worker.
            await AsyncOperationDeadline.RunAsync(
                ct => WaitForHangUpAsync(server, ct), answerTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            // The answer is written; a sender that neither reads nor leaves is simply closed on.
        }
    }

    /// <summary>
    /// Connect, write the payload line, and read the acknowledgement line within its bound. Returns
    /// null when the server closed without answering. Throws <see cref="TimeoutException"/> only for
    /// a missing answer; a server that never accepted the connection surfaces as
    /// <see cref="IOException"/>, and a pipe another user owns as
    /// <see cref="UnauthorizedAccessException"/>, so the sender logs either as unreachable, not as
    /// silence.
    /// </summary>
    public static async Task<string?> ExchangeAsync(string pipeName, string? payload, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, Options);
        try
        {
            await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, token).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new IOException("No running instance accepted the hand-off connection.", ex);
        }

        using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

        await WriteLineAsync(client, payload ?? string.Empty, token).ConfigureAwait(false);
        return await AsyncOperationDeadline.RunAsync(
            ct => reader.ReadLineAsync(ct).AsTask(),
            SingleInstanceHandoffPolicy.AckTimeout, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Read one payload line: the bytes before the first newline, or everything up to EOF. Returns
    /// null when the line is longer than <see cref="SingleInstancePipePolicy.MaxPayloadBytes"/>; the
    /// rest of that line is still read and discarded so its sender reaches the point of reading the
    /// answer, but never more than the limit is kept.
    /// </summary>
    internal static async Task<string?> ReadPayloadLineAsync(Stream stream, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // One spare byte so a line exactly at the limit may still end in CRLF: the CR is framing,
        // stripped below, and never counts against the payload.
        var line = new byte[SingleInstancePipePolicy.MaxPayloadBytes + 1];
        var chunk = new byte[1024];
        var length = 0;
        var overlong = false;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) break;

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            var take = newline < 0 ? read : newline;
            if (!overlong && length + take <= line.Length)
            {
                Buffer.BlockCopy(chunk, 0, line, length, take);
                length += take;
            }
            else
            {
                overlong = true;
            }

            if (newline >= 0) break;
        }

        if (length > 0 && line[length - 1] == (byte)'\r') length--;
        if (overlong || length > SingleInstancePipePolicy.MaxPayloadBytes) return null;
        return Encoding.UTF8.GetString(line, 0, length);
    }

    private static async Task<bool> WaitForHangUpAsync(PipeStream pipe, CancellationToken token)
    {
        var sink = new byte[64];
        while (await pipe.ReadAsync(sink, token).ConfigureAwait(false) > 0)
        {
            // Nothing more is expected; anything a sender adds after its line is discarded.
        }
        return true;
    }

    private static async Task WriteLineAsync(PipeStream pipe, string line, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }
}
