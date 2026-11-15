namespace FocusRemote.Protocol;

/// <summary>Fixed values of the FocusRemote v1 wire protocol (see docs/Protocol-vi.md).</summary>
public static class ProtocolConstants
{
    public const byte Version = 1;
    public const int NonceLength = 32;
    public const int SaltLength = 16;
    public const int KeyLength = 32;
    public const int TagLength = 16;
    public const int CounterLength = 8;
    public const int Pbkdf2Iterations = 600_000;
    public const int MinPasswordLength = 10;
    public const int MaxMessageSize = 16 * 1024 * 1024;
    /// <summary>AAD of every Sealed frame: the single byte 0x10.</summary>
    public const byte SealedAad = 0x10;
    public const string SaltPrefix = "FocusRemote/v1/salt/";
    public const string SessionInfo = "FocusRemote/v1/session";
    public const string AuthProbeText = "FocusRemote/v1/probe";
}
