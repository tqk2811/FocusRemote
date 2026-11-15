namespace FocusRemote.Protocol.Enums;

/// <summary>Message types of the Audio channel.</summary>
public enum AudioType : byte
{
    Config = 0x01,
    Packet = 0x02,
}
