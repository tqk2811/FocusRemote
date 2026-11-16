namespace FocusRemote.Relay.Relay;

/// <summary>Relay settings, bound from the "Relay" section of appsettings.json.</summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>Maximum size of one assembled binary message (default 16 MiB).</summary>
    public int MaxMessageBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Viewer connection attempts allowed per client IP per minute.</summary>
    public int ViewerConnectPerMinute { get; set; } = 20;

    /// <summary>WebSocket keep-alive (ping) interval in seconds.</summary>
    public int KeepAliveSeconds { get; set; } = 15;

    /// <summary>Honor X-Forwarded-For/Proto (enable only behind a trusted reverse proxy).</summary>
    public bool UseForwardedHeaders { get; set; }

    /// <summary>Header holding the client IP when <see cref="UseForwardedHeaders"/> is on; "CF-Connecting-IP" behind Cloudflare.</summary>
    public string ForwardedForHeader { get; set; } = "X-Forwarded-For";

    /// <summary>Proxies whose forwarded headers are honored (IP or CIDR, e.g. Cloudflare ranges). Empty = any proxy.</summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>Capacity (in messages) of each per-direction forwarding queue.</summary>
    public int QueueCapacity { get; set; } = 64;

    /// <summary>
    /// Maximum total bytes of binary messages waiting in each per-direction queue (default 32 MiB).
    /// A message larger than this is still accepted when the queue is empty.
    /// </summary>
    public long QueueMaxBytes { get; set; } = 32L * 1024 * 1024;
}
