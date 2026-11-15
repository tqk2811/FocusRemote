namespace FocusRemote.Protocol.Enums;

/// <summary>Outermost frame kind (first byte of every binary message).</summary>
public enum FrameKind : byte
{
    HelloViewer = 0x01,
    HelloHost = 0x02,
    AuthFail = 0x03,
    Sealed = 0x10,
}
