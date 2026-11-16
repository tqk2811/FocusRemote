namespace FocusRemote.Protocol.Messages.Models.Terminal;

/// <summary>
/// Terminal 0x12 Closed (H→V). <see cref="Reason"/>: <c>exited</c>, <c>closed</c>, <c>disabled</c>,
/// <c>shell_not_found</c>, <c>start_failed</c>, <c>limit</c>, <c>duplicate_id</c>.
/// </summary>
public sealed class TerminalClosedMessage
{
    public required uint TerminalId { get; init; }
    public required string Reason { get; init; }
    /// <summary>Process exit code when <see cref="Reason"/> = <c>exited</c>.</summary>
    public int? ExitCode { get; init; }
    public string? Message { get; init; }
}
