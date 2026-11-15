namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>Control 0x30 Error (H→V).</summary>
public sealed class ErrorMessage
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}
