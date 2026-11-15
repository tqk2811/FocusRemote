using FocusRemote.Protocol.Messages.Models;

namespace FocusRemote.Host.Core.Audio.Interfaces;

/// <summary>Receives the output of an audio streamer. Called on the streamer thread.</summary>
public interface IAudioSink
{
    /// <summary>Sent once before the first packet (Audio Config, protocol §5.4).</summary>
    void OnAudioConfig(AudioConfigMessage message);

    /// <summary>Audio channel Packet payload (timestamp + raw Opus).</summary>
    void OnAudioPacket(byte[] payload);
}
