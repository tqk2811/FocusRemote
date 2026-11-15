using FocusRemote.Host.Core.Sources.Models;

namespace FocusRemote.Host.Core.Video.Interfaces;

/// <summary>A running capture+encode stream of one source. Dispose stops it without reporting StreamStopped.</summary>
public interface IVideoStreamer : IDisposable
{
    CaptureSource Source { get; }

    void Start();

    void RequestKeyframe();
}
