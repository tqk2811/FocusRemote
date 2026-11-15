using System.Net.WebSockets;
using System.Text;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Handshake;
using FocusRemote.Protocol.Messages;

namespace FocusRemote.Host.Tests.Helpers;

/// <summary>Minimal C# viewer over a WebSocket (relay or LAN): handshake with <see cref="ViewerHandshake"/>, then sealed messages.</summary>
internal sealed class TestViewer : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly WebSocket _socket;
    private SecureSession? _session;

    public TestViewer(WebSocket socket)
    {
        _socket = socket;
    }

    public SecureSession Session => _session ?? throw new InvalidOperationException("Not authenticated.");

    public async Task ExpectStatusAsync(string type)
    {
        (WebSocketMessageType kind, byte[] data, _) = await ReceiveAsync();
        Assert.Equal(WebSocketMessageType.Text, kind);
        Assert.Equal("{\"type\":\"" + type + "\"}", System.Text.Encoding.UTF8.GetString(data));
    }

    /// <summary>Runs the handshake; returns true on AuthOk-ready (session set) or false on AuthFail.</summary>
    public async Task<bool> HandshakeAsync(byte[] rootKey)
    {
        var handshake = new ViewerHandshake(rootKey);
        await SendAsync(handshake.CreateHelloViewer());
        (_, byte[] helloHost, _) = await ReceiveAsync();
        Assert.True(handshake.TryHandleHelloHost(helloHost, out byte[] probe));
        await SendAsync(probe);
        (WebSocketMessageType kind, byte[] reply, _) = await ReceiveAsync();
        Assert.Equal(WebSocketMessageType.Binary, kind);
        if (HandshakeFrames.IsAuthFail(reply))
            return false;
        _session = handshake.Session!;
        Assert.True(_session.TryOpen(reply, out byte channel, out byte type, out _));
        Assert.Equal((byte)Channel.Control, channel);
        Assert.Equal((byte)ControlType.AuthOk, type);
        return true;
    }

    public Task SendControlAsync<T>(ControlType type, T message)
        => SendAsync(Session.Seal(Channel.Control, (byte)type, ControlMessages.SerializeJson(message)));

    /// <summary>Receives sealed messages until one matches; returns its payload.</summary>
    public async Task<byte[]> ReceiveUntilAsync(Channel channel, byte type, Func<byte[], bool>? predicate = null)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (true)
        {
            (WebSocketMessageType kind, byte[] data, WebSocketCloseStatus? close) = await ReceiveAsync(cts.Token);
            Assert.True(kind != WebSocketMessageType.Close, $"Socket closed ({close}) while waiting for {channel}/{type:X2}");
            if (kind != WebSocketMessageType.Binary)
                continue;
            Assert.True(Session.TryOpen(data, out byte c, out byte t, out byte[] payload));
            if (c == (byte)channel && t == type && (predicate is null || predicate(payload)))
                return payload;
        }
    }

    public Task SendAsync(byte[] data)
    {
        using var cts = new CancellationTokenSource(Timeout);
        return _socket.SendAsync(data, WebSocketMessageType.Binary, true, cts.Token);
    }

    public async Task<(WebSocketMessageType Type, byte[] Data, WebSocketCloseStatus? CloseStatus)> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        using var stream = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return (result.MessageType, [], result.CloseStatus);
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return (result.MessageType, stream.ToArray(), null);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _socket.Dispose();
    }
}
