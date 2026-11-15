using System.Buffers.Binary;
using System.Security.Cryptography;
using FocusRemote.Protocol.Crypto.Models;
using FocusRemote.Protocol.Enums;

namespace FocusRemote.Protocol.Crypto;

/// <summary>
/// An established encrypted session: seals outgoing and opens incoming Sealed frames (protocol §3, §5).
/// Not thread-safe: callers serialize <see cref="Seal"/> calls and <see cref="TryOpen"/> calls separately
/// (send and receive use independent state, so one sender thread plus one receiver thread is fine).
/// Owns its <see cref="AesGcm"/> instances and disposes them.
/// </summary>
public sealed class SecureSession : IDisposable
{
    private const int HeaderLength = 1 + ProtocolConstants.CounterLength;
    private static readonly byte[] _aad = [ProtocolConstants.SealedAad];

    private readonly AesGcm _sendCipher;
    private readonly AesGcm _receiveCipher;
    private readonly object _sendLock = new();
    private ulong _sendCounter;
    private ulong _lastReceivedCounter;
    private bool _hasReceived;

    public SessionRole Role { get; }

    /// <summary>Counter that the next <see cref="Seal"/> will use.</summary>
    public ulong NextSendCounter => _sendCounter;

    public SecureSession(SessionKeys keys, SessionRole role)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Role = role;
        byte[] sendKey = role == SessionRole.Host ? keys.KeyH2V : keys.KeyV2H;
        byte[] receiveKey = role == SessionRole.Host ? keys.KeyV2H : keys.KeyH2V;
        _sendCipher = new AesGcm(sendKey, ProtocolConstants.TagLength);
        _receiveCipher = new AesGcm(receiveKey, ProtocolConstants.TagLength);
    }

    /// <summary>Builds a Sealed frame: <c>0x10 ‖ counter u64 LE ‖ ciphertext ‖ tag</c>.</summary>
    public byte[] Seal(byte channel, byte type, ReadOnlySpan<byte> payload)
    {
        int plainLength = 2 + payload.Length;
        byte[] frame = new byte[HeaderLength + plainLength + ProtocolConstants.TagLength];
        byte[] plain = new byte[plainLength];
        plain[0] = channel;
        plain[1] = type;
        payload.CopyTo(plain.AsSpan(2));

        lock (_sendLock)
        {
            ulong counter = _sendCounter++;
            frame[0] = (byte)FrameKind.Sealed;
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1, 8), counter);
            Span<byte> nonce = stackalloc byte[12];
            BuildNonce(counter, nonce);
            _sendCipher.Encrypt(
                nonce,
                plain,
                frame.AsSpan(HeaderLength, plainLength),
                frame.AsSpan(HeaderLength + plainLength),
                _aad);
        }
        return frame;
    }

    public byte[] Seal(Channel channel, byte type, ReadOnlySpan<byte> payload) => Seal((byte)channel, type, payload);

    /// <summary>
    /// Opens a Sealed frame. Returns false if the frame is not Sealed, is malformed, fails authentication,
    /// or replays a counter not greater than the last accepted one. Per spec a false result on a
    /// Sealed frame means the session must be closed.
    /// The channel/type are returned raw so unknown values never throw; check with <c>Enum.IsDefined</c>.
    /// </summary>
    public bool TryOpen(ReadOnlySpan<byte> frame, out byte channel, out byte type, out byte[] payload)
    {
        channel = 0;
        type = 0;
        payload = [];
        if (frame.Length < HeaderLength + 2 + ProtocolConstants.TagLength || frame[0] != (byte)FrameKind.Sealed)
            return false;

        ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(1, 8));
        if (_hasReceived && counter <= _lastReceivedCounter)
            return false;

        int plainLength = frame.Length - HeaderLength - ProtocolConstants.TagLength;
        byte[] plain = new byte[plainLength];
        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(counter, nonce);
        try
        {
            _receiveCipher.Decrypt(
                nonce,
                frame.Slice(HeaderLength, plainLength),
                frame.Slice(HeaderLength + plainLength),
                plain,
                _aad);
        }
        catch (CryptographicException)
        {
            return false;
        }

        _lastReceivedCounter = counter;
        _hasReceived = true;
        channel = plain[0];
        type = plain[1];
        payload = plain[2..];
        return true;
    }

    public void Dispose()
    {
        _sendCipher.Dispose();
        _receiveCipher.Dispose();
    }

    private static void BuildNonce(ulong counter, Span<byte> nonce)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, counter);
    }
}
