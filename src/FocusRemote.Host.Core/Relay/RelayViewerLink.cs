using System.Threading.Channels;
using FocusRemote.Host.Core.Sessions.Interfaces;

namespace FocusRemote.Host.Core.Relay;

/// <summary>
/// The viewer currently paired on the external relay (between <c>peer-joined</c> and <c>peer-left</c>).
/// Incoming binary messages are posted by <see cref="RelayHostClient"/>'s receive loop; sends go out on the
/// shared host socket. <see cref="Close"/> only detaches: the relay socket stays open for the next viewer.
/// </summary>
internal sealed class RelayViewerLink : IViewerLink
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Func<byte[], CancellationToken, Task> _send;
    private volatile bool _closed;

    public RelayViewerLink(Func<byte[], CancellationToken, Task> send)
    {
        _send = send;
    }

    public string Origin => "relay";

    public ChannelReader<byte[]> Incoming => _incoming.Reader;

    public bool IsClosed => _closed;

    public void Post(byte[] message)
    {
        if (!_closed)
            _incoming.Writer.TryWrite(message);
    }

    public Task SendAsync(byte[] message, CancellationToken cancellationToken)
        => _closed ? Task.CompletedTask : _send(message, cancellationToken);

    public void Close()
    {
        _closed = true;
        _incoming.Writer.TryComplete();
    }
}
