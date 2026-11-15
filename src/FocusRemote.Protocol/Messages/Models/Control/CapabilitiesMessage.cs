namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>Control 0x03 Capabilities (V→H). Values: <c>h264</c>, <c>h265</c>, <c>opus</c>.</summary>
public sealed class CapabilitiesMessage
{
    public required IReadOnlyList<string> VideoCodecs { get; init; }
    public required IReadOnlyList<string> AudioCodecs { get; init; }
}
