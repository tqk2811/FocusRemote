using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace FocusRemote.Relay.Relay;

/// <summary>
/// Wraps one accepted WebSocket. All sends go through a bounded queue drained by a single
/// writer loop, so writers await (backpressure) instead of buffering unbounded memory.
/// Does not own the socket's lifetime beyond closing it; ASP.NET disposes it.
/// </summary>
internal sealed class PeerConnection
{
    private enum OutKind { Binary, Text, Close }

    private readonly record struct OutMessage(OutKind Kind, byte[] Data, int Length, WebSocketCloseStatus CloseStatus);

    private readonly WebSocket _socket;
    private readonly Channel<OutMessage> _queue;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _abort = new();

    public PeerConnection(WebSocket socket, int queueCapacity)
    {
        _socket = socket;
        _queue = Channel.CreateBounded<OutMessage>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public WebSocket Socket => _socket;

    /// <summary>Queues a binary message; awaits while the queue is full. Silently ignored once closed.</summary>
    public async ValueTask SendBinaryAsync(byte[] data, int length, CancellationToken cancellationToken)
    {
        try
        {
            await _queue.Writer.WriteAsync(new OutMessage(OutKind.Binary, data, length, default), cancellationToken);
        }
        catch (ChannelClosedException)
        {
        }
    }

    /// <summary>Queues a relay status text message <c>{"type":"..."}</c> (ordered with binary data).</summary>
    public Task SendStatusAsync(string type)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"type\":\"" + type + "\"}");
        return WriteControlAsync(new OutMessage(OutKind.Text, bytes, bytes.Length, default));
    }

    /// <summary>Queues a close frame after everything already queued; no more messages are accepted.</summary>
    public Task CloseAsync(WebSocketCloseStatus status)
    {
        return WriteControlAsync(new OutMessage(OutKind.Close, Array.Empty<byte>(), 0, status), complete: true);
    }

    private async Task WriteControlAsync(OutMessage message, bool complete = false)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _queue.Writer.WriteAsync(message, timeout.Token);
            if (complete)
                _queue.Writer.TryComplete();
        }
        catch (ChannelClosedException)
        {
        }
        catch (OperationCanceledException)
        {
            // Receiver stuck; give up on this socket.
            _queue.Writer.TryComplete();
            _abort.Cancel();
        }
    }

    /// <summary>Stops accepting messages and waits for the writer to finish.</summary>
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _writerTask;
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (OutMessage message in _queue.Reader.ReadAllAsync(_abort.Token))
            {
                switch (message.Kind)
                {
                    case OutKind.Binary:
                        await _socket.SendAsync(new ArraySegment<byte>(message.Data, 0, message.Length),
                            WebSocketMessageType.Binary, true, _abort.Token);
                        break;
                    case OutKind.Text:
                        await _socket.SendAsync(message.Data, WebSocketMessageType.Text, true, _abort.Token);
                        break;
                    case OutKind.Close:
                        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                            await _socket.CloseOutputAsync(message.CloseStatus, null, _abort.Token);
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Socket gone: stop sending, unblock waiting producers.
        }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) { }
        }
    }
}
