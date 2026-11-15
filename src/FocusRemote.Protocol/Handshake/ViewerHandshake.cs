using System.Security.Cryptography;
using System.Text;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Handshake.Enums;

namespace FocusRemote.Protocol.Handshake;

/// <summary>
/// Viewer side of the handshake (protocol §4). After <see cref="TryHandleHelloHost"/> succeeds the
/// caller sends the probe frame, then waits for either an AuthFail frame
/// (<see cref="HandshakeFrames.IsAuthFail"/>) or a Sealed Control/AuthOk opened with <see cref="Session"/>.
/// The caller owns and disposes <see cref="Session"/>.
/// </summary>
public sealed class ViewerHandshake
{
    private readonly byte[] _rootKey;
    private readonly Func<byte[]> _nonceFactory;
    private byte[]? _nonceV;

    public HandshakeState State { get; private set; } = HandshakeState.Initial;

    public SecureSession? Session { get; private set; }

    /// <param name="rootKey">32-byte root key from <see cref="KeyDerivation.DeriveRootKey"/>.</param>
    /// <param name="nonceFactory">Nonce source for tests; defaults to a CSPRNG.</param>
    public ViewerHandshake(byte[] rootKey, Func<byte[]>? nonceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        _rootKey = rootKey;
        _nonceFactory = nonceFactory ?? (() => RandomNumberGenerator.GetBytes(ProtocolConstants.NonceLength));
    }

    /// <summary>Creates the HelloViewer frame (call once, after relay reports <c>paired</c>).</summary>
    public byte[] CreateHelloViewer()
    {
        if (State != HandshakeState.Initial)
            throw new InvalidOperationException("HelloViewer already created.");
        _nonceV = _nonceFactory();
        State = HandshakeState.WaitingPeer;
        return HandshakeFrames.BuildHello(FrameKind.HelloViewer, _nonceV);
    }

    /// <summary>Handles HelloHost, derives the session and returns the sealed AuthProbe frame to send.</summary>
    public bool TryHandleHelloHost(ReadOnlySpan<byte> frame, out byte[] probeFrame)
    {
        probeFrame = [];
        if (State != HandshakeState.WaitingPeer || !HandshakeFrames.TryParseHello(frame, FrameKind.HelloHost, out byte[] nonceH))
            return false;
        Session = new SecureSession(KeyDerivation.DeriveSessionKeys(_rootKey, _nonceV, nonceH), SessionRole.Viewer);
        probeFrame = Session.Seal(Channel.Control, (byte)ControlType.AuthProbe, Encoding.UTF8.GetBytes(ProtocolConstants.AuthProbeText));
        State = HandshakeState.Completed;
        return true;
    }
}
