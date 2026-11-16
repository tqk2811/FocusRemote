namespace FocusRemote.Protocol.Enums;

/// <summary>Outermost frame kind (first byte of every binary message).</summary>
public enum FrameKind : byte
{
    HelloViewer = 0x01,
    HelloHost = 0x02,
    AuthFail = 0x03,
    /// <summary>Host already serves another viewer; nothing more follows for this viewer.</summary>
    Busy = 0x04,
    Sealed = 0x10,
}
