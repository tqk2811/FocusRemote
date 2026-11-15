using FocusRemote.Protocol.Enums;

namespace FocusRemote.Protocol.Messages.Models.Input;

/// <summary><see cref="Button"/> is raw so unknown buttons (≥5) parse without throwing; host ignores them.</summary>
public sealed class MouseButtonInput : InputMessage
{
    public override InputType Type => InputType.MouseButton;
    public required MouseButton Button { get; init; }
    public required bool Down { get; init; }
    public required ushort X { get; init; }
    public required ushort Y { get; init; }
}
