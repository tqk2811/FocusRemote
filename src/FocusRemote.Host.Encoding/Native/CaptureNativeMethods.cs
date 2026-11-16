using System.Runtime.InteropServices;

namespace FocusRemote.Host.Encoding.Native;

/// <summary>P/Invoke bindings for the FrCapture_* functions of FocusRemote.Encoder.Native.dll. All cdecl.</summary>
internal static class CaptureNativeMethods
{
	const string DllName = "FocusRemote.Encoder.Native";

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern CaptureSafeHandle FrCapture_Create();

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrCapture_Init(CaptureSafeHandle capture, EncoderSafeHandle encoder, int kind, IntPtr handle, int showCursor);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrCapture_Poll(CaptureSafeHandle capture);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void FrCapture_GetSize(CaptureSafeHandle capture, out int width, out int height);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrCapture_HasFrame(CaptureSafeHandle capture);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int FrCapture_IsClosed(CaptureSafeHandle capture);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr FrCapture_GetLastError(CaptureSafeHandle capture);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void FrCapture_Destroy(IntPtr capture);
}
