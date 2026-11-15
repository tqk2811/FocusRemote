namespace FocusRemote.Protocol.Enums;

/// <summary>Message types of the Input channel.</summary>
public enum InputType : byte
{
    MouseMove = 0x01,
    MouseButton = 0x02,
    MouseWheel = 0x03,
    Key = 0x04,
    Text = 0x05,
    ReleaseAll = 0x06,
}
