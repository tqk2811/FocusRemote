using FocusRemote.Protocol.Enums;

namespace FocusRemote.Protocol.Messages.Models.Input;

/// <summary><see cref="Code"/> is the browser <c>KeyboardEvent.code</c> (ASCII, max 255 chars), e.g. <c>KeyA</c>.</summary>
public sealed class KeyInput : InputMessage
{
    public override InputType Type => InputType.Key;
    public required bool Down { get; init; }
    public required string Code { get; init; }
}
