using System.Threading.Channels;
using FocusRemote.Host.Core.Sessions.Interfaces;
using FocusRemote.Host.Core.Sessions.Models;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Enums;
using Channel = FocusRemote.Protocol.Enums.Channel;

namespace FocusRemote.Host.Core.Sessions;

/// <summary>
/// Send queue of one viewer session. A single writer seals messages right before sending them, so the
/// Sealed counters go out strictly increasing. Counts queued video frames so the streamer can drop frames
/// when the link is congested (protocol §5.2: more than <see cref="MaxPendingVideoFrames"/> unsent frames).
/// </summary>
internal sealed class OutboundQueue
{
    public const int MaxPendingVideoFrames = 2;

    private readonly Channel<OutboundMessage> _channel = System.Threading.Channels.Channel.CreateUnbounded<OutboundMessage>(new UnboundedChannelOptions { SingleReader = true });
    private int _pendingVideoFrames;

    public int PendingVideoFrames => Volatile.Read(ref _pendingVideoFrames);

    public bool IsCongested => PendingVideoFrames > MaxPendingVideoFrames;

    public void Enqueue(Channel channel, byte type, byte[] payload)
        => _channel.Writer.TryWrite(new OutboundMessage((byte)channel, type, payload, false));

    public void EnqueueVideoFrame(byte[] payload)
    {
        Interlocked.Increment(ref _pendingVideoFrames);
        if (!_channel.Writer.TryWrite(new OutboundMessage((byte)Channel.Video, (byte)VideoType.Frame, payload, true)))
            Interlocked.Decrement(ref _pendingVideoFrames);
    }

    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>Drains the queue until <see cref="Complete"/> or cancellation; send errors end the loop.</summary>
    public async Task RunWriterAsync(SecureSession session, IViewerLink link, CancellationToken cancellationToken)
    {
        await foreach (OutboundMessage message in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                byte[] frame = session.Seal(message.Channel, message.Type, message.Payload);
                await link.SendAsync(frame, cancellationToken);
            }
            finally
            {
                if (message.IsVideoFrame)
                    Interlocked.Decrement(ref _pendingVideoFrames);
            }
        }
    }
}
