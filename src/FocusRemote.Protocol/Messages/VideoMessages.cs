using System.Buffers.Binary;
using FocusRemote.Protocol.Messages.Models;

namespace FocusRemote.Protocol.Messages;

/// <summary>Video channel payload codec (protocol §5.2).</summary>
public static class VideoMessages
{
    private const int HeaderLength = 9;

    public static byte[] BuildFrame(bool isKeyframe, ulong timestampUs, ReadOnlySpan<byte> data)
    {
        byte[] payload = new byte[HeaderLength + data.Length];
        payload[0] = isKeyframe ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1, 8), timestampUs);
        data.CopyTo(payload.AsSpan(HeaderLength));
        return payload;
    }

    public static byte[] BuildFrame(VideoFrameMessage message) => BuildFrame(message.IsKeyframe, message.TimestampUs, message.Data.Span);

    /// <summary>Unknown flag bits are ignored. <see cref="VideoFrameMessage.Data"/> slices <paramref name="payload"/> (no copy).</summary>
    public static bool TryParseFrame(byte[] payload, out VideoFrameMessage? message)
    {
        message = null;
        if (payload is null || payload.Length < HeaderLength)
            return false;
        message = new VideoFrameMessage
        {
            IsKeyframe = (payload[0] & 1) != 0,
            TimestampUs = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(1, 8)),
            Data = payload.AsMemory(HeaderLength),
        };
        return true;
    }
}
