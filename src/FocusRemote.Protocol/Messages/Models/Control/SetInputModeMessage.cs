namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>Control 0x17 SetInputMode (V→H): how the host injects input for window sources.</summary>
public sealed class SetInputModeMessage
{
    /// <summary>Global SendInput (default; also used by old viewers that never send this message).</summary>
    public const string SendInput = "sendinput";

    /// <summary>PostMessage to the streamed window; the host's real mouse/keyboard stay untouched.</summary>
    public const string Message = "message";

    /// <summary><see cref="SendInput"/> or <see cref="Message"/>; any other value is treated as <see cref="SendInput"/>.</summary>
    public required string Mode { get; init; }
}
