using FocusRemote.Host.Core.Relay.Enums;
using FocusRemote.Host.Core.Video.Models;

namespace FocusRemote.Host.Core.Models;

/// <summary>Immutable snapshot of what the UI shows.</summary>
public sealed record HostStatus
{
    public bool IsRunning { get; init; }
    public bool RelayEnabled { get; init; }
    public RelayState RelayState { get; init; }
    public string? RelayDetail { get; init; }
    public bool LanRunning { get; init; }
    public string? LanError { get; init; }
    public IReadOnlyList<string> LanUrls { get; init; } = [];
    public int LanPort { get; init; }
    /// <summary>Origin of the authenticated viewer; null when none.</summary>
    public string? Viewer { get; init; }
    public StreamStats? Stream { get; init; }
}
