using System.Runtime.InteropServices;
using FocusRemote.Host.Encoding.Native.Models;

namespace FocusRemote.Host.Encoding.Native;

/// <summary>P/Invoke bindings for FocusRemote.Encoder.Native.dll (see FocusRemoteEncoder.h). All cdecl.</summary>
internal static class EncoderNativeMethods
{
	const string DllName = "FocusRemote.Encoder.Native";

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern EncoderSafeHandle FrEncoder_Create();

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrEncoder_Init(EncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrEncoder_Open(EncoderSafeHandle encoder, in NativeEncoderOptions options);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr FrEncoder_GetInputSurface(EncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrEncoder_GetSurfaceGeneration(EncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrEncoder_Encode(EncoderSafeHandle encoder, int forceKeyframe, long timestampUs, out NativePacket packet);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr FrEncoder_GetEncoderName(EncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrEncoder_IsZeroCopy(EncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr FrEncoder_GetAdapterName(EncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern uint FrEncoder_GetAdapterVendorId(EncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr FrEncoder_GetLastError(EncoderSafeHandle encoder);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void FrEncoder_Destroy(IntPtr encoder);
}
