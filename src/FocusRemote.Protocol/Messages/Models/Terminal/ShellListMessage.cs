namespace FocusRemote.Protocol.Messages.Models.Terminal;

/// <summary>Terminal 0x02 ShellList (H→V). <see cref="Enabled"/> = false when the host turned the terminal off (then <see cref="Shells"/> is empty).</summary>
public sealed class ShellListMessage
{
    public required bool Enabled { get; init; }
    public required IReadOnlyList<ShellInfo> Shells { get; init; }
}
