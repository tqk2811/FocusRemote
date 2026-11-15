namespace FocusRemote.Protocol.Messages.Models;

/// <summary>Video/Frame payload: <c>flags u8 (bit0 keyframe) ‖ timestampUs u64 ‖ H.264 Annex B</c>.</summary>
public sealed class VideoFrameMessage
{
    public required bool IsKeyframe { get; init; }
    public required ulong TimestampUs { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }
}
