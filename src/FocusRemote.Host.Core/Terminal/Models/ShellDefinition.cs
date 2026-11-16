namespace FocusRemote.Host.Core.Terminal.Models;

/// <summary>One startable shell. <see cref="CommandLine"/> never comes from the viewer.</summary>
public sealed class ShellDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string CommandLine { get; init; }
}
