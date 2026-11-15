namespace FocusRemote.Host.Core.Video.Models;

/// <summary>Snapshot of the running stream, refreshed about once per second.</summary>
public sealed record StreamStats(string SourceId, string EncoderName, int Width, int Height, double Fps, double BitrateKbps, int DroppedFrames);
