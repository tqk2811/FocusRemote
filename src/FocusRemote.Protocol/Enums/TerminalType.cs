namespace FocusRemote.Protocol.Enums;

/// <summary>Message types of the Terminal channel (protocol §5.5).</summary>
public enum TerminalType : byte
{
    ListShells = 0x01,
    ShellList = 0x02,
    Open = 0x10,
    Opened = 0x11,
    Closed = 0x12,
    Input = 0x20,
    Output = 0x21,
    Resize = 0x22,
    Close = 0x23,
}
