namespace FocusRemote.Host.Core.Audio.Interfaces;

/// <summary>A running capture+encode audio stream. Dispose stops it.</summary>
public interface IAudioStreamer : IDisposable
{
    void Start();
}
