namespace FocusRemote.Protocol.Messages.Models.Terminal;

/// <summary>One shell the host can start, e.g. <c>{"id":"pwsh","name":"PowerShell 7"}</c>.</summary>
public sealed class ShellInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}
