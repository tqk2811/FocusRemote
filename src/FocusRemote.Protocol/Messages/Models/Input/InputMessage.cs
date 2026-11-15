using FocusRemote.Protocol.Enums;

namespace FocusRemote.Protocol.Messages.Models.Input;

/// <summary>Base of all Input channel messages. Coordinates are u16 normalized: 0 = left/top, 65535 = right/bottom.</summary>
public abstract class InputMessage
{
    public abstract InputType Type { get; }
}
