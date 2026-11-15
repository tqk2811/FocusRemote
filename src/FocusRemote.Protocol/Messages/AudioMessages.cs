using System.Buffers.Binary;
using FocusRemote.Protocol.Messages.Models;

namespace FocusRemote.Protocol.Messages;

/// <summary>Audio channel payload codec (protocol §5.4). Config uses <see cref="ControlMessages.SerializeJson"/>.</summary>
public static class AudioMessages
{
    public static byte[] BuildConfig(AudioConfigMessage message) => ControlMessages.SerializeJson(message);

    public static bool TryParseConfig(ReadOnlySpan<byte> payload, out AudioConfigMessage? message)
        => ControlMessages.TryDeserializeJson(payload, out message);

    public static byte[] BuildPacket(ulong timestampUs, ReadOnlySpan<byte> opus)
    {
        byte[] payload = new byte[8 + opus.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, timestampUs);
        opus.CopyTo(payload.AsSpan(8));
        return payload;
    }

    /// <summary><see cref="AudioPacketMessage.Data"/> slices <paramref name="payload"/> (no copy).</summary>
    public static bool TryParsePacket(byte[] payload, out AudioPacketMessage? message)
    {
        message = null;
        if (payload is null || payload.Length < 8)
            return false;
        message = new AudioPacketMessage
        {
            TimestampUs = BinaryPrimitives.ReadUInt64LittleEndian(payload),
            Data = payload.AsMemory(8),
        };
        return true;
    }
}
