using System.IO;
using System.IO.Pipes;
using System.Text;

namespace PiPlay.Services;

/// <summary>
/// The wire of the single-instance hand-off (spec 11, PP-05): one line in, one line back, over one
/// duplex named pipe per request. Kept free of App state so the real framing runs in the test lane.
/// </summary>
internal static class SingleInstancePipeTransport
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Accept one connection, read the payload line (bounded by the client read timeout; a legacy
    /// client that writes without a newline and closes still yields its text at EOF), dispatch it,
    /// and answer with the acknowledgement line. The answer is drained before the pipe closes so a
    /// sender that is still reading gets it rather than a broken pipe.
    /// </summary>
    public static async Task ServeOneAsync(
        string pipeName,
        Func<string, CancellationToken, Task<HandoffAck>> dispatchAsync,
        Action<HandoffAck, Exception> onAckUndeliverable,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(dispatchAsync);
        ArgumentNullException.ThrowIfNull(onAckUndeliverable);

        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
        var payload = await SingleInstancePipePolicy.ReadClientPayloadAsync(
            async ct => await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? string.Empty,
            token).ConfigureAwait(false);

        var ack = await dispatchAsync(payload, token).ConfigureAwait(false);
        try
        {
            // Raw bytes, no StreamWriter: its Dispose flushes and throws "Pipe is broken" when the
            // sender already left, which would turn a served request into a server failure.
            await WriteLineAsync(server, SingleInstanceHandoffPolicy.ToWire(ack), token)
                .WaitAsync(SingleInstanceHandoffPolicy.AckTimeout, token).ConfigureAwait(false);
            server.WaitForPipeDrain();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            onAckUndeliverable(ack, ex);
        }
    }

    /// <summary>
    /// Connect, write the payload line, and read the acknowledgement line within its bound. Returns
    /// null when the server closed without answering. Throws <see cref="TimeoutException"/> only for
    /// a missing answer; a server that never accepted the connection surfaces as
    /// <see cref="IOException"/> so the sender logs it as unreachable, not as silence.
    /// </summary>
    public static async Task<string?> ExchangeAsync(string pipeName, string? payload, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
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
        return await reader.ReadLineAsync(token).AsTask()
            .WaitAsync(SingleInstanceHandoffPolicy.AckTimeout, token).ConfigureAwait(false);
    }

    private static async Task WriteLineAsync(PipeStream pipe, string line, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }
}
