namespace FocusRemote.Host.Core.Settings.Models;

/// <summary>Persisted host settings. The password is never stored: only the DPAPI-protected root key.</summary>
public sealed class HostSettings
{
    public const int DefaultLanPort = 8443;

    /// <summary>External relay base URL (http/https/ws/wss); empty = no external relay.</summary>
    public string RelayUrl { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    /// <summary>Base64 of the DPAPI-protected (CurrentUser) PBKDF2 root key derived from <see cref="Id"/> + password.</summary>
    public string? ProtectedRootKey { get; set; }

    public bool LanEnabled { get; set; }

    public int LanPort { get; set; } = DefaultLanPort;

    /// <summary>Bind the LAN server to 127.0.0.1 only (tests/E2E harness: no firewall prompt). Not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LanLoopbackOnly { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>Allow remote terminals (full command execution on this machine). Off by default.</summary>
    public bool TerminalEnabled { get; set; }
}
