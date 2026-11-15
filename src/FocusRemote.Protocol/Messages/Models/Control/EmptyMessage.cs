namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>Payload <c>{}</c> used by ListSources, StopStream and RequestKeyframe.</summary>
public sealed class EmptyMessage
{
    public static EmptyMessage Instance { get; } = new();
}
