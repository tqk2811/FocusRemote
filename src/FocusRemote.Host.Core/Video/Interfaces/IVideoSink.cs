using FocusRemote.Host.Core.Video.Models;
using FocusRemote.Protocol.Messages.Models.Control;

namespace FocusRemote.Host.Core.Video.Interfaces;

/// <summary>Receives the output of a <see cref="VideoStreamer"/>. Called on the streamer thread.</summary>
public interface IVideoSink
{
    /// <summary>True while too many frames wait to be sent; the streamer then skips frames and forces a keyframe.</summary>
    bool IsCongested { get; }

    void OnStreamStarted(StreamStartedMessage message);

    /// <summary>Video channel Frame payload (flags, timestamp, Annex B).</summary>
    void OnFrame(byte[] payload);

    /// <summary>The stream ended by itself (window closed, capture/encode error). Not called on <see cref="VideoStreamer.Dispose"/>.</summary>
    void OnStreamStopped(string reason, ErrorMessage? error);

    void OnStats(StreamStats stats);
}
