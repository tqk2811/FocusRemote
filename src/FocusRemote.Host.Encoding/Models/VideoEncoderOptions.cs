using FocusRemote.Host.Encoding.Enums;

namespace FocusRemote.Host.Encoding.Models;

public sealed class VideoEncoderOptions
{
	public VideoCodec Codec { get; init; } = VideoCodec.H264;
	/// <summary>Even, &gt; 0. Also the size of <see cref="HardwareVideoEncoder.InputSurface"/>.</summary>
	public required int Width { get; init; }
	/// <summary>Even, &gt; 0.</summary>
	public required int Height { get; init; }
	public int Fps { get; init; } = 30;
	public int BitrateKbps { get; init; } = 4000;
	/// <summary>Periodic keyframe interval; &lt;= 0 means keyframes only when forced.</summary>
	public int GopSeconds { get; init; } = 5;
	/// <summary>Force an FFmpeg encoder by name (e.g. <c>h264_mf</c>); null picks one from the adapter vendor.</summary>
	public string? EncoderName { get; init; }
}
