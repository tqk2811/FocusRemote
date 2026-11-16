namespace FocusRemote.Protocol.Messages.Models.Terminal;

/// <summary>Terminal 0x22 Resize (V→H), size in character cells.</summary>
public sealed class TerminalResizeMessage
{
    public required uint TerminalId { get; init; }
    public required ushort Cols { get; init; }
    public required ushort Rows { get; init; }
}
