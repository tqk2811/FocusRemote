namespace FocusRemote.Host.Encoding.Models;

/// <summary>
/// One encoded access unit (Annex B). <see cref="Data"/> points into native memory owned by the encoder
/// and is only valid until the next call on that <see cref="HardwareVideoEncoder"/>: copy it before then.
/// </summary>
public readonly ref struct EncodedPacket
{
	public EncodedPacket(ReadOnlySpan<byte> data, bool isKeyframe, long timestampUs)
	{
		Data = data;
		IsKeyframe = isKeyframe;
		TimestampUs = timestampUs;
	}

	public ReadOnlySpan<byte> Data { get; }
	public bool IsKeyframe { get; }
	public long TimestampUs { get; }
}
