using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Core.Sources.Enums;
using FocusRemote.Protocol.Messages.Models.Control;

namespace FocusRemote.Host.Core.Sources.Models;

/// <summary>A capturable window or monitor. <see cref="Handle"/> is the HWND or HMONITOR.</summary>
public sealed class CaptureSource
{
    public required string Id { get; init; }
    public required SourceKind Kind { get; init; }
    public required string Title { get; init; }
    public required IntPtr Handle { get; init; }
    /// <summary>Bounds in physical pixels at enumeration time (window: DWM extended frame bounds).</summary>
    public required PixelRect Bounds { get; init; }

    public SourceInfo ToSourceInfo() => new()
    {
        Id = Id,
        Kind = Kind == SourceKind.Window ? "window" : "monitor",
        Title = Title,
        Width = Bounds.Width,
        Height = Bounds.Height,
    };
}
