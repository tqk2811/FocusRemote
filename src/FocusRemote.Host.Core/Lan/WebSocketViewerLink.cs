using System.Net.WebSockets;
using System.Threading.Channels;
using FocusRemote.Host.Core.Sessions.Interfaces;

namespace FocusRemote.Host.Core.Lan;

/// <summary>
/// A viewer connected straight to the in-process LAN server: the WebSocket is the link (no relay hop).
/// <see cref="PumpAsync"/> is the only reader of the socket; sends are serialized by a lock.
/// </summary>
internal sealed class WebSocketViewerLink : IViewerLink
{
    /// <summary>Viewer → host messages are small (input/control); anything bigger is refused.</summary>
    private const int MaxIncomingMessage = 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _abort = new();
    private int _closed;

    public WebSocketViewerLink(WebSocket socket, string origin)
    {
        _socket = socket;
        Origin = origin;
    }

    public string Origin { get; }

    public ChannelReader<byte[]> Incoming => _incoming.Reader;

    public async Task SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Sends a relay-style status text (<c>{"type":"paired"}</c>) so the viewer runs the same code path as with the relay.</summary>
    public async Task SendStatusAsync(string type, CancellationToken cancellationToken)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"" + type + "\"}");
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Reads the socket into <see cref="Incoming"/> until it closes or <see cref="Close"/> is called.</summary>
    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abort.Token);
        byte[] chunk = new byte[16 * 1024];
        try
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(chunk, linked.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (buffer.Length + result.Count > MaxIncomingMessage)
                    break;
                buffer.Write(chunk, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                if (result.MessageType == WebSocketMessageType.Binary)
                    _incoming.Writer.TryWrite(buffer.ToArray());
                buffer.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _incoming.Writer.TryComplete();
        }
    }

    /// <summary>Starts a close handshake; aborts the pump if the viewer does not answer within 3 s.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _incoming.Writer.TryComplete();
        _ = CloseCoreAsync();
    }

    public Task CloseWithStatusAsync(WebSocketCloseStatus status)
    {
        Interlocked.Exchange(ref _closed, 1);
        return CloseOutputAsync(status);
    }

    private async Task CloseCoreAsync()
    {
        await CloseOutputAsync(WebSocketCloseStatus.NormalClosure);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            _abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task CloseOutputAsync(WebSocketCloseStatus status)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(status, null, timeout.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}
