namespace FocusRemote.Protocol.Enums;

/// <summary>Channel of a Sealed plaintext. Values 4-255 are reserved; receivers ignore them.</summary>
public enum Channel : byte
{
    Control = 0,
    Video = 1,
    Input = 2,
    Audio = 3,
}
