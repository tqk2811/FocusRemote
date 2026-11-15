namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>Control 0x13 StreamStarted (H→V).</summary>
public sealed class StreamStartedMessage
{
    public required string SourceId { get; init; }
    public required string Codec { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }
}
