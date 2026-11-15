using Microsoft.Win32.SafeHandles;

namespace FocusRemote.Host.Encoding.Native;

/// <summary>Owns a native <c>FrEncoder*</c>.</summary>
internal sealed class EncoderSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
	public EncoderSafeHandle() : base(ownsHandle: true)
	{
	}

	protected override bool ReleaseHandle()
	{
		EncoderNativeMethods.FrEncoder_Destroy(handle);
		return true;
	}
}
