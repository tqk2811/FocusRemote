namespace FocusRemote.Protocol.Messages.Models.Terminal;

/// <summary>Terminal 0x10 Open (V→H). <see cref="TerminalId"/> is chosen by the viewer and unique within the session.</summary>
public sealed class TerminalOpenMessage
{
    public required uint TerminalId { get; init; }
    public required string ShellId { get; init; }
    public required int Cols { get; init; }
    public required int Rows { get; init; }
}
