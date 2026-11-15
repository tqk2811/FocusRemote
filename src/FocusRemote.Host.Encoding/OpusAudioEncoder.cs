using System.Runtime.InteropServices;
using FocusRemote.Host.Encoding.Models;
using FocusRemote.Host.Encoding.Native;
using FocusRemote.Host.Encoding.Native.Models;

namespace FocusRemote.Host.Encoding;

/// <summary>
/// Opus encoder (FFmpeg libopus, low-delay application) over the native DLL: one frame of interleaved float PCM in,
/// raw Opus packets out (protocol §5.4 Audio Packet). Not thread-safe. Owns its native context; dispose it.
/// </summary>
public sealed class OpusAudioEncoder : IDisposable
{
	readonly AudioEncoderSafeHandle _handle;

	/// <exception cref="AudioEncoderException">No Opus encoder could be opened with these options.</exception>
	public OpusAudioEncoder(AudioEncoderOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_handle = AudioEncoderNativeMethods.FrAudioEncoder_Create();
		if (_handle.IsInvalid)
			throw new OutOfMemoryException("FrAudioEncoder_Create failed");
		NativeAudioEncoderOptions native = new()
		{
			SampleRate = options.SampleRate,
			Channels = options.Channels,
			BitrateKbps = options.BitrateKbps,
			FrameDurationMs = options.FrameDurationMs,
		};
		if (AudioEncoderNativeMethods.FrAudioEncoder_Open(_handle, in native) != 0)
		{
			string error = LastError;
			_handle.Dispose();
			throw new AudioEncoderException(error);
		}
		SampleRate = options.SampleRate;
		Channels = options.Channels;
		FrameSamples = AudioEncoderNativeMethods.FrAudioEncoder_GetFrameSamples(_handle);
	}

	public int SampleRate { get; }
	public int Channels { get; }
	/// <summary>Samples per channel in each frame passed to <see cref="Encode"/> (960 for 20 ms at 48 kHz).</summary>
	public int FrameSamples { get; }
	/// <summary>FFmpeg encoder in use: <c>libopus</c>, or <c>opus</c> (native, experimental) as fallback.</summary>
	public string EncoderName => Marshal.PtrToStringUTF8(AudioEncoderNativeMethods.FrAudioEncoder_GetEncoderName(_handle)) ?? string.Empty;

	string LastError => Marshal.PtrToStringUTF8(AudioEncoderNativeMethods.FrAudioEncoder_GetLastError(_handle)) ?? string.Empty;

	/// <summary>
	/// Encodes one frame (<see cref="FrameSamples"/> × <see cref="Channels"/> interleaved floats) and calls
	/// <paramref name="onPacket"/> for every packet produced (data valid only during the call).
	/// </summary>
	public unsafe void Encode(ReadOnlySpan<float> interleaved, long timestampUs, PacketHandler onPacket)
	{
		if (interleaved.Length != FrameSamples * Channels)
			throw new ArgumentException($"Expected {FrameSamples * Channels} samples, got {interleaved.Length}.", nameof(interleaved));
		fixed (float* samples = interleaved)
		{
			if (AudioEncoderNativeMethods.FrAudioEncoder_Send(_handle, samples, FrameSamples, timestampUs) != 0)
				throw new AudioEncoderException(LastError);
		}
		while (true)
		{
			int result = AudioEncoderNativeMethods.FrAudioEncoder_Receive(_handle, out NativePacket native);
			if (result < 0)
				throw new AudioEncoderException(LastError);
			if (result == 0)
				return;
			onPacket(new ReadOnlySpan<byte>((void*)native.Data, native.Size), native.Pts);
		}
	}

	public void Dispose() => _handle.Dispose();

	/// <summary>Receives one raw Opus packet; <paramref name="data"/> is only valid during the call.</summary>
	public delegate void PacketHandler(ReadOnlySpan<byte> data, long timestampUs);
}
