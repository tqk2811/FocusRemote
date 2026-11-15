namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>Control 0x12 SelectSource (V→H).</summary>
public sealed class SelectSourceMessage
{
    public required string Id { get; init; }
    public required int MaxWidth { get; init; }
    public required int MaxHeight { get; init; }
    public required int MaxFps { get; init; }
    public required int BitrateKbps { get; init; }
    public required bool Audio { get; init; }
}
