namespace FocusRemote.Protocol.Messages.Models.Terminal;

/// <summary>Terminal 0x20 Input / 0x21 Output: raw bytes of one terminal (UTF-8 text with VT sequences).</summary>
public sealed class TerminalDataMessage
{
    public required uint TerminalId { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }
}
