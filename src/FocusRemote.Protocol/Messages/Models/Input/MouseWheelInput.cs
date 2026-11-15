using FocusRemote.Protocol.Enums;

namespace FocusRemote.Protocol.Messages.Models.Input;

/// <summary>Deltas in WHEEL_DELTA units (120 per notch); positive = up / right.</summary>
public sealed class MouseWheelInput : InputMessage
{
    public override InputType Type => InputType.MouseWheel;
    public required short DeltaX { get; init; }
    public required short DeltaY { get; init; }
    public required ushort X { get; init; }
    public required ushort Y { get; init; }
}
