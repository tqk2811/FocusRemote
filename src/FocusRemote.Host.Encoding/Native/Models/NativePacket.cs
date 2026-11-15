using System.Runtime.InteropServices;

namespace FocusRemote.Host.Encoding.Native.Models;

/// <summary>Mirror of <c>FrPacket</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativePacket
{
	public IntPtr Data;
	public int Size;
	public int IsKeyframe;
	public long Pts;
}
