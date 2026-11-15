using System.Runtime.InteropServices;

namespace FocusRemote.Host.Encoding.Native.Models;

/// <summary>Mirror of <c>FrAudioEncoderOptions</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeAudioEncoderOptions
{
	public int SampleRate;
	public int Channels;
	public int BitrateKbps;
	public int FrameDurationMs;
}
