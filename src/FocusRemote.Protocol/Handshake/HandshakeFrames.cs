using FocusRemote.Protocol.Enums;

namespace FocusRemote.Protocol.Handshake;

/// <summary>Builders/parsers for the unencrypted outer frames (protocol §2).</summary>
public static class HandshakeFrames
{
    private const int HelloLength = 2 + ProtocolConstants.NonceLength;

    /// <summary>Returns the frame kind byte, or null for an empty frame. Unknown kinds are returned as-is.</summary>
    public static byte? PeekKind(ReadOnlySpan<byte> frame) => frame.IsEmpty ? null : frame[0];

    /// <summary><c>kind ‖ version(1) ‖ nonce(32)</c> for HelloViewer or HelloHost.</summary>
    public static byte[] BuildHello(FrameKind kind, ReadOnlySpan<byte> nonce)
    {
        if (kind is not (FrameKind.HelloViewer or FrameKind.HelloHost))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a hello kind.");
        if (nonce.Length != ProtocolConstants.NonceLength)
            throw new ArgumentException("Nonce must be 32 bytes.", nameof(nonce));
        byte[] frame = new byte[HelloLength];
        frame[0] = (byte)kind;
        frame[1] = ProtocolConstants.Version;
        nonce.CopyTo(frame.AsSpan(2));
        return frame;
    }

    /// <summary>Parses a hello of the expected kind and version 1; false otherwise.</summary>
    public static bool TryParseHello(ReadOnlySpan<byte> frame, FrameKind expectedKind, out byte[] nonce)
    {
        nonce = [];
        if (frame.Length != HelloLength || frame[0] != (byte)expectedKind || frame[1] != ProtocolConstants.Version)
            return false;
        nonce = frame[2..].ToArray();
        return true;
    }

    public static byte[] BuildAuthFail() => [(byte)FrameKind.AuthFail];

    public static bool IsAuthFail(ReadOnlySpan<byte> frame) => frame.Length == 1 && frame[0] == (byte)FrameKind.AuthFail;
}
