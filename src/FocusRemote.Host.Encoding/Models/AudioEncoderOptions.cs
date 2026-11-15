namespace FocusRemote.Host.Encoding.Models;

/// <summary>Settings for <see cref="OpusAudioEncoder"/>. Defaults: 48 kHz stereo, 20 ms frames, 96 kbps.</summary>
public sealed class AudioEncoderOptions
{
	public int SampleRate { get; init; } = 48000;
	/// <summary>1 or 2.</summary>
	public int Channels { get; init; } = 2;
	public int BitrateKbps { get; init; } = 96;
	public int FrameDurationMs { get; init; } = 20;
}
