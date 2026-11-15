using System.Runtime.InteropServices;

namespace FocusRemote.Host.Encoding.Native.Models;

/// <summary>Mirror of <c>FrEncoderOptions</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeEncoderOptions
{
	public int Codec;
	public int Width;
	public int Height;
	public int Fps;
	public int BitrateKbps;
	public int GopSeconds;
	/// <summary>UTF-8 string or zero.</summary>
	public IntPtr EncoderName;
}
