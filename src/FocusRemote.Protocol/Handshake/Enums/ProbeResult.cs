namespace FocusRemote.Protocol.Handshake.Enums;

/// <summary>Outcome of <see cref="HostHandshake.HandleProbe"/>.</summary>
public enum ProbeResult
{
    /// <summary>Frame was not processed (wrong state or not a Sealed frame); nothing to send.</summary>
    Ignored,
    /// <summary>Password correct; <see cref="HostHandshake.Session"/> is ready. Send AuthOk then SourceList.</summary>
    Accepted,
    /// <summary>Password wrong or frame invalid; send the AuthFail frame and drop the session.</summary>
    Rejected,
}
