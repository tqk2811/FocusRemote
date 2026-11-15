using FocusRemote.Protocol.Enums;

namespace FocusRemote.Protocol.Messages.Models.Input;

public sealed class MouseMoveInput : InputMessage
{
    public override InputType Type => InputType.MouseMove;
    public required ushort X { get; init; }
    public required ushort Y { get; init; }
}
