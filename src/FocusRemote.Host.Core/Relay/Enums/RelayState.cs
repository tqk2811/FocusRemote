namespace FocusRemote.Host.Core.Relay.Enums;

public enum RelayState
{
    Stopped,
    Connecting,
    Registered,
    /// <summary>Disconnected or rejected; waiting before the next attempt.</summary>
    WaitingRetry,
}
