namespace FocusRemote.Protocol.Handshake.Enums;

/// <summary>State of a <see cref="HostHandshake"/> or <see cref="ViewerHandshake"/>.</summary>
public enum HandshakeState
{
    /// <summary>Host: waiting for HelloViewer. Viewer: HelloViewer not created yet.</summary>
    Initial,
    /// <summary>Host: HelloHost sent, waiting for AuthProbe. Viewer: HelloViewer sent, waiting for HelloHost.</summary>
    WaitingPeer,
    /// <summary>Host: probe accepted. Viewer: probe sent (AuthOk/AuthFail still pending at app level).</summary>
    Completed,
    /// <summary>Host: probe rejected, AuthFail sent; drop this session.</summary>
    Failed,
}
