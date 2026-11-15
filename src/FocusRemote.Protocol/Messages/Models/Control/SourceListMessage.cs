namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>Control 0x11 SourceList (H→V).</summary>
public sealed class SourceListMessage
{
    public required IReadOnlyList<SourceInfo> Sources { get; init; }
}
