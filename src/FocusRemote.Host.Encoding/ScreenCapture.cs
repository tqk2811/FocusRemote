using System.Drawing;
using System.Runtime.InteropServices;
using FocusRemote.Host.Encoding.Enums;
using FocusRemote.Host.Encoding.Native;

namespace FocusRemote.Host.Encoding;

/// <summary>
/// Windows.Graphics.Capture of one window or monitor, running on the D3D11 device of a <see cref="HardwareVideoEncoder"/>
/// so <see cref="HardwareVideoEncoder.Encode"/> reads the frame without any cross-device sharing.
/// Not thread-safe: call <see cref="Poll"/> and Encode from the same thread. Owns its native context; dispose it.
/// Does not own the encoder (it keeps its own reference to the device, so dispose order does not matter).
/// </summary>
public sealed class ScreenCapture : IDisposable
{
	readonly CaptureSafeHandle _handle;

	/// <exception cref="VideoEncoderException">Capture could not start (unsupported OS, invalid handle...).</exception>
	public ScreenCapture(HardwareVideoEncoder encoder, CaptureTargetKind kind, IntPtr handle, bool showCursor = true)
	{
		ArgumentNullException.ThrowIfNull(encoder);
		_handle = CaptureNativeMethods.FrCapture_Create();
		if (_handle.IsInvalid)
			throw new OutOfMemoryException("FrCapture_Create failed");
		if (CaptureNativeMethods.FrCapture_Init(_handle, encoder.Handle, (int)kind, handle, showCursor ? 1 : 0) != 0)
		{
			string error = LastError;
			_handle.Dispose();
			throw new VideoEncoderException(error);
		}
	}

	/// <summary>Content size of the latest frame; before the first frame, the size of the capture item.</summary>
	public Size Size
	{
		get
		{
			CaptureNativeMethods.FrCapture_GetSize(_handle, out int width, out int height);
			return new Size(width, height);
		}
	}

	/// <summary>True once at least one frame has been received (Encode needs one).</summary>
	public bool HasFrame => CaptureNativeMethods.FrCapture_HasFrame(_handle) != 0;

	/// <summary>True when the captured window/monitor went away.</summary>
	public bool IsClosed => CaptureNativeMethods.FrCapture_IsClosed(_handle) != 0;

	internal CaptureSafeHandle Handle => _handle;

	string LastError => Marshal.PtrToStringUTF8(CaptureNativeMethods.FrCapture_GetLastError(_handle)) ?? string.Empty;

	/// <summary>
	/// Takes the newest pending frame (older ones are dropped). Returns true when a new frame arrived.
	/// The source only delivers frames when its content changes.
	/// </summary>
	public bool Poll()
	{
		int result = CaptureNativeMethods.FrCapture_Poll(_handle);
		if (result < 0)
			throw new VideoEncoderException(LastError);
		return result > 0;
	}

	public void Dispose() => _handle.Dispose();
}
