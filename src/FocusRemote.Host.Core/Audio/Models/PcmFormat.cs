namespace FocusRemote.Host.Core.Audio.Models;

/// <summary>Interleaved PCM layout of a capture stream.</summary>
public readonly record struct PcmFormat(bool IsFloat, int BitsPerSample, int Channels, int SampleRate);
