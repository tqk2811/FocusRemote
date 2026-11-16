using System.Net.WebSockets;
using FocusRemote.Relay.Relay;

namespace FocusRemote.Relay.Tests;

public sealed class PeerConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SendBinary_WaitsWhileQueuedBytesExceedBudget()
    {
        var socket = new GatedWebSocket();
        var peer = new PeerConnection(socket, queueCapacity: 64, maxQueuedBytes: 10);

        // First message is taken by the writer and blocks inside SendAsync; its bytes stay counted.
        await peer.SendBinaryAsync(new byte[6], 6, CancellationToken.None).AsTask().WaitAsync(Timeout);
        await socket.SendStarted.Task.WaitAsync(Timeout);

        // 6 + 6 > 10: the second message must wait for budget, even though the message count is far below 64.
        Task second = peer.SendBinaryAsync(new byte[6], 6, CancellationToken.None).AsTask();
        await Task.Delay(200);
        Assert.False(second.IsCompleted);

        socket.Release.TrySetResult();
        await second.WaitAsync(Timeout);

        await peer.CompleteAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task SendBinary_AcceptsOversizeMessageWhenQueueEmpty()
    {
        var socket = new GatedWebSocket();
        socket.Release.TrySetResult();
        var peer = new PeerConnection(socket, queueCapacity: 64, maxQueuedBytes: 10);

        await peer.SendBinaryAsync(new byte[100], 100, CancellationToken.None).AsTask().WaitAsync(Timeout);
        await peer.CompleteAsync().WaitAsync(Timeout);
        Assert.Equal(1, socket.SentCount);
    }

    /// <summary>WebSocket whose SendAsync blocks until <see cref="Release"/> is completed.</summary>
    private sealed class GatedWebSocket : WebSocket
    {
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SentCount;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref SentCount);
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Abort() { }
        public override void Dispose() { }
    }
}
