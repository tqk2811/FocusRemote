using System.Threading.Channels;

namespace FocusRemote.Host.Core.Sessions.Interfaces;

/// <summary>
/// Transport to one viewer, independent of how it is reached (external relay WebSocket, or the in-process
/// LAN server). Carries whole binary protocol messages only; relay status text never reaches the session.
/// </summary>
public interface IViewerLink
{
    /// <summary>Human readable origin for the UI/log, e.g. <c>relay</c> or <c>LAN 192.168.1.5</c>.</summary>
    string Origin { get; }

    /// <summary>Binary messages from the viewer; completes when the viewer is gone.</summary>
    ChannelReader<byte[]> Incoming { get; }

    /// <summary>Sends one binary message. Only one send at a time is issued by the session.</summary>
    Task SendAsync(byte[] message, CancellationToken cancellationToken);

    /// <summary>Stops talking to this viewer (LAN: closes the socket; relay: just detaches). Idempotent.</summary>
    void Close();
}
