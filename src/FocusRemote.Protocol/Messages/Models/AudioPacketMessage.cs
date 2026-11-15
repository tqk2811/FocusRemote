namespace FocusRemote.Protocol.Messages.Models;

/// <summary>Audio/Packet payload: <c>timestampUs u64 ‖ raw Opus packet</c>.</summary>
public sealed class AudioPacketMessage
{
    public required ulong TimestampUs { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }
}
