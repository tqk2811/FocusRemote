using FocusRemote.Protocol.Enums;

namespace FocusRemote.Protocol.Messages.Models.Input;

/// <summary>UTF-8 text typed via KEYEVENTF_UNICODE (mobile virtual keyboard).</summary>
public sealed class TextInput : InputMessage
{
    public override InputType Type => InputType.Text;
    public required string Text { get; init; }
}
