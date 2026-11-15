namespace FocusRemote.Protocol.Enums;

/// <summary>Message types of the Control channel.</summary>
public enum ControlType : byte
{
    AuthProbe = 0x01,
    AuthOk = 0x02,
    Capabilities = 0x03,
    ListSources = 0x10,
    SourceList = 0x11,
    SelectSource = 0x12,
    StreamStarted = 0x13,
    StopStream = 0x14,
    StreamStopped = 0x15,
    RequestKeyframe = 0x16,
    Ping = 0x20,
    Pong = 0x21,
    Error = 0x30,
}
