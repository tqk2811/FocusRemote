using Microsoft.Win32.SafeHandles;

namespace FocusRemote.Host.Encoding.Native;

/// <summary>Owns a native <c>FrCapture*</c>.</summary>
internal sealed class CaptureSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
	public CaptureSafeHandle() : base(ownsHandle: true)
	{
	}

	protected override bool ReleaseHandle()
	{
		CaptureNativeMethods.FrCapture_Destroy(handle);
		return true;
	}
}
