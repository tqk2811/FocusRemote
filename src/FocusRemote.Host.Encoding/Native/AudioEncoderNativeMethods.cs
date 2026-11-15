using System.Runtime.InteropServices;
using FocusRemote.Host.Encoding.Native.Models;

namespace FocusRemote.Host.Encoding.Native;

/// <summary>P/Invoke bindings for the Opus part of FocusRemote.Encoder.Native.dll (see FocusRemoteEncoder.h). All cdecl.</summary>
internal static class AudioEncoderNativeMethods
{
	const string DllName = "FocusRemote.Encoder.Native";

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern AudioEncoderSafeHandle FrAudioEncoder_Create();

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrAudioEncoder_Open(AudioEncoderSafeHandle encoder, in NativeAudioEncoderOptions options);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrAudioEncoder_GetFrameSamples(AudioEncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern unsafe int FrAudioEncoder_Send(AudioEncoderSafeHandle encoder, float* samples, int samplesPerChannel, long timestampUs);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrAudioEncoder_Receive(AudioEncoderSafeHandle encoder, out NativePacket packet);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr FrAudioEncoder_GetEncoderName(AudioEncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr FrAudioEncoder_GetLastError(AudioEncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void FrAudioEncoder_Destroy(IntPtr encoder);
}
