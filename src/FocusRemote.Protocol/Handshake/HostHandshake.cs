using System.Security.Cryptography;
using System.Text;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Handshake.Enums;

namespace FocusRemote.Protocol.Handshake;

/// <summary>
/// Host side of the handshake (protocol §4) for one viewer connection. Create a new instance per viewer.
/// The resulting <see cref="Session"/> is handed over to the caller, who then owns and disposes it.
/// Failure delay is not applied here; use <see cref="AuthFailureDelayPolicy"/>.
/// </summary>
public sealed class HostHandshake
{
    private readonly byte[] _rootKey;
    private readonly Func<byte[]> _nonceFactory;
    private byte[]? _nonceV;
    private byte[]? _nonceH;

    public HandshakeState State { get; private set; } = HandshakeState.Initial;

    /// <summary>Established session after <see cref="ProbeResult.Accepted"/>; owned by the caller.</summary>
    public SecureSession? Session { get; private set; }

    /// <param name="rootKey">32-byte root key from <see cref="KeyDerivation.DeriveRootKey"/>.</param>
    /// <param name="nonceFactory">Nonce source for tests; defaults to a CSPRNG.</param>
    public HostHandshake(byte[] rootKey, Func<byte[]>? nonceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        _rootKey = rootKey;
        _nonceFactory = nonceFactory ?? (() => RandomNumberGenerator.GetBytes(ProtocolConstants.NonceLength));
    }

    /// <summary>Handles HelloViewer; on success returns the HelloHost frame to send.</summary>
    public bool TryHandleHelloViewer(ReadOnlySpan<byte> frame, out byte[] helloHost)
    {
        helloHost = [];
        if (State != HandshakeState.Initial || !HandshakeFrames.TryParseHello(frame, FrameKind.HelloViewer, out byte[] nonceV))
            return false;
        _nonceV = nonceV;
        _nonceH = _nonceFactory();
        helloHost = HandshakeFrames.BuildHello(FrameKind.HelloHost, _nonceH);
        State = HandshakeState.WaitingPeer;
        return true;
    }

    /// <summary>
    /// Verifies the viewer's first Sealed frame (Control/AuthProbe). On <see cref="ProbeResult.Rejected"/>,
    /// <paramref name="authFailFrame"/> holds the AuthFail frame to send.
    /// </summary>
    public ProbeResult HandleProbe(ReadOnlySpan<byte> frame, out byte[]? authFailFrame)
    {
        authFailFrame = null;
        if (State != HandshakeState.WaitingPeer || frame.IsEmpty || frame[0] != (byte)FrameKind.Sealed)
            return ProbeResult.Ignored;

        var session = new SecureSession(KeyDerivation.DeriveSessionKeys(_rootKey, _nonceV, _nonceH), SessionRole.Host);
        if (session.TryOpen(frame, out byte channel, out byte type, out byte[] payload)
            && channel == (byte)Channel.Control
            && type == (byte)ControlType.AuthProbe
            && payload.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(ProtocolConstants.AuthProbeText)))
        {
            Session = session;
            State = HandshakeState.Completed;
            return ProbeResult.Accepted;
        }

        session.Dispose();
        State = HandshakeState.Failed;
        authFailFrame = HandshakeFrames.BuildAuthFail();
        return ProbeResult.Rejected;
    }
}
