using FocusRemote.Protocol.Enums;

namespace FocusRemote.Protocol.Messages.Models.Input;

/// <summary>Release every held key/button (viewer lost focus). Empty payload.</summary>
public sealed class ReleaseAllInput : InputMessage
{
    public override InputType Type => InputType.ReleaseAll;
}
