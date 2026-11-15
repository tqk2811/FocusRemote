using Microsoft.Win32.SafeHandles;

namespace FocusRemote.Host.Encoding.Native;

/// <summary>Owns a native <c>FrAudioEncoder*</c>.</summary>
internal sealed class AudioEncoderSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
	public AudioEncoderSafeHandle() : base(ownsHandle: true)
	{
	}

	protected override bool ReleaseHandle()
	{
		AudioEncoderNativeMethods.FrAudioEncoder_Destroy(handle);
		return true;
	}
}
