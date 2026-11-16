namespace FocusRemote.Protocol.Messages.Models.Terminal;

/// <summary>Terminal 0x11 Opened (H→V).</summary>
public sealed class TerminalOpenedMessage
{
    public required uint TerminalId { get; init; }
    public required string ShellId { get; init; }
}
