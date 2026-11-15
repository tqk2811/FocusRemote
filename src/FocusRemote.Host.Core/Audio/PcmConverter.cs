using FocusRemote.Host.Core.Audio.Models;

namespace FocusRemote.Host.Core.Audio;

/// <summary>Converts captured interleaved PCM (16/24/32-bit integer or 32-bit float, 1..N channels) to interleaved float.</summary>
public static class PcmConverter
{
    /// <summary>
    /// Appends the samples of <paramref name="source"/> to <paramref name="destination"/> as interleaved floats with
    /// <paramref name="outputChannels"/> channels (mono is duplicated; extra input channels are dropped).
    /// A trailing partial frame is ignored. Returns the number of frames (samples per channel) appended.
    /// </summary>
    public static int AppendAsFloat(ReadOnlySpan<byte> source, PcmFormat format, int outputChannels, List<float> destination)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int blockAlign = bytesPerSample * format.Channels;
        if (blockAlign <= 0 || outputChannels <= 0)
            return 0;
        int frames = source.Length / blockAlign;
        destination.EnsureCapacity(destination.Count + frames * outputChannels);
        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<byte> block = source.Slice(f * blockAlign, blockAlign);
            for (int c = 0; c < outputChannels; c++)
            {
                int inputChannel = Math.Min(c, format.Channels - 1);
                destination.Add(ReadSample(block.Slice(inputChannel * bytesPerSample, bytesPerSample), format));
            }
        }
        return frames;
    }

    private static float ReadSample(ReadOnlySpan<byte> s, PcmFormat format)
    {
        if (format.IsFloat)
            return format.BitsPerSample == 32 ? BitConverter.ToSingle(s) : 0f;
        return format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(s) / 32768f,
            24 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
            32 => BitConverter.ToInt32(s) / 2147483648f,
            8 => (s[0] - 128) / 128f,
            _ => 0f,
        };
    }
}
