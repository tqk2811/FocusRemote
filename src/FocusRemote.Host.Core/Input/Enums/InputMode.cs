namespace FocusRemote.Host.Core.Input.Enums;

/// <summary>How viewer input reaches a streamed window (protocol Control SetInputMode).</summary>
public enum InputMode
{
    /// <summary>Global SendInput; the window is brought to the foreground first.</summary>
    SendInput,

    /// <summary>PostMessage to the window/child under the point; monitor sources still use SendInput.</summary>
    Message,
}
