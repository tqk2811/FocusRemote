using System.Runtime.InteropServices;
using FocusRemote.Host.Encoding.Models;
using FocusRemote.Host.Encoding.Native;
using FocusRemote.Host.Encoding.Native.Models;

namespace FocusRemote.Host.Encoding;

/// <summary>
/// Zero-copy screen encoder: a capture library renders into <see cref="InputSurface"/> (shared D3D11 BGRA texture
/// on DXGI adapter 0), then <see cref="Encode"/> converts it to NV12 on the GPU and runs the FFmpeg hardware encoder.
/// Not thread-safe: call Open/Render/Encode from one thread. Owns its native context; dispose it.
/// </summary>
public sealed class HardwareVideoEncoder : IDisposable
{
	readonly EncoderSafeHandle _handle;

	/// <exception cref="VideoEncoderException">The D3D11 device could not be created.</exception>
	public HardwareVideoEncoder()
	{
		_handle = EncoderNativeMethods.FrEncoder_Create();
		if (_handle.IsInvalid)
			throw new OutOfMemoryException("FrEncoder_Create failed");
		if (EncoderNativeMethods.FrEncoder_Init(_handle) != 0)
		{
			string error = LastError;
			_handle.Dispose();
			throw new VideoEncoderException(error);
		}
	}

	public string AdapterName => ReadString(EncoderNativeMethods.FrEncoder_GetAdapterName(_handle));
	/// <summary>PCI vendor id of the adapter: 0x10DE NVIDIA, 0x8086 Intel, 0x1002 AMD.</summary>
	public uint AdapterVendorId => EncoderNativeMethods.FrEncoder_GetAdapterVendorId(_handle);
	/// <summary>FFmpeg encoder in use (e.g. <c>h264_nvenc</c>); empty before <see cref="Open"/>.</summary>
	public string EncoderName => ReadString(EncoderNativeMethods.FrEncoder_GetEncoderName(_handle));
	/// <summary>False when the encoder needs system-memory frames (NV12 read back from the GPU).</summary>
	public bool IsZeroCopy => EncoderNativeMethods.FrEncoder_IsZeroCopy(_handle) != 0;
	/// <summary>IDXGISurface* to pass to <c>WinrtGraphicCapture.Render</c>. Borrowed: valid until the next <see cref="Open"/> or <see cref="Dispose"/>.</summary>
	public IntPtr InputSurface => EncoderNativeMethods.FrEncoder_GetInputSurface(_handle);
	/// <summary>Changes whenever <see cref="Open"/> recreates <see cref="InputSurface"/>.</summary>
	public int SurfaceGeneration => EncoderNativeMethods.FrEncoder_GetSurfaceGeneration(_handle);

	string LastError => ReadString(EncoderNativeMethods.FrEncoder_GetLastError(_handle));

	/// <summary>Opens (or re-opens with new settings) the encoder and recreates <see cref="InputSurface"/>.</summary>
	public void Open(VideoEncoderOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		IntPtr encoderName = options.EncoderName is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(options.EncoderName);
		try
		{
			NativeEncoderOptions native = new()
			{
				Codec = (int)options.Codec,
				Width = options.Width,
				Height = options.Height,
				Fps = options.Fps,
				BitrateKbps = options.BitrateKbps,
				GopSeconds = options.GopSeconds,
				EncoderName = encoderName,
			};
			if (EncoderNativeMethods.FrEncoder_Open(_handle, in native) != 0)
				throw new VideoEncoderException(LastError);
		}
		finally
		{
			Marshal.FreeCoTaskMem(encoderName);
		}
	}

	/// <summary>
	/// Encodes what is currently in <see cref="InputSurface"/>.
	/// Returns false when the encoder buffered the frame without output (not expected with the low-latency settings).
	/// </summary>
	public unsafe bool Encode(bool forceKeyframe, long timestampUs, out EncodedPacket packet)
	{
		int result = EncoderNativeMethods.FrEncoder_Encode(_handle, forceKeyframe ? 1 : 0, timestampUs, out NativePacket native);
		if (result < 0)
			throw new VideoEncoderException(LastError);
		if (result == 0)
		{
			packet = default;
			return false;
		}
		packet = new EncodedPacket(new ReadOnlySpan<byte>((void*)native.Data, native.Size), native.IsKeyframe != 0, native.Pts);
		return true;
	}

	public void Dispose() => _handle.Dispose();

	static string ReadString(IntPtr utf8) => Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
}
