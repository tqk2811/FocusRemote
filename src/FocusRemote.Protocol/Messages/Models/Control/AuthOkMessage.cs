namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>Control 0x02 AuthOk (H→V).</summary>
public sealed class AuthOkMessage
{
    public required string HostName { get; init; }
    public required string Version { get; init; }
}
