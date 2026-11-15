namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>Control 0x15 StreamStopped (H→V).</summary>
public sealed class StreamStoppedMessage
{
    public required string SourceId { get; init; }
    public required string Reason { get; init; }
}
