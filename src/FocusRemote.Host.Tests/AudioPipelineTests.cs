using FocusRemote.Host.Core.Audio;
using FocusRemote.Host.Core.Audio.Models;
using FocusRemote.Host.Encoding;
using FocusRemote.Host.Encoding.Models;

namespace FocusRemote.Host.Tests;

public sealed class AudioPipelineTests
{
    [Fact]
    public void PcmConverter_Int16Stereo_ToFloat()
    {
        byte[] pcm = [0x00, 0x80, 0xFF, 0x7F, 0x00, 0x40, 0x00, 0x00, 0x01]; // 2 frames + 1 trailing byte
        var output = new List<float>();
        int frames = PcmConverter.AppendAsFloat(pcm, new PcmFormat(false, 16, 2, 48000), 2, output);
        Assert.Equal(2, frames);
        Assert.Equal([-1f, 32767 / 32768f, 0.5f, 0f], output);
    }

    [Fact]
    public void PcmConverter_FloatMono_DuplicatedToStereo()
    {
        byte[] pcm = [.. BitConverter.GetBytes(0.25f), .. BitConverter.GetBytes(-0.5f)];
        var output = new List<float>();
        PcmConverter.AppendAsFloat(pcm, new PcmFormat(true, 32, 1, 48000), 2, output);
        Assert.Equal([0.25f, 0.25f, -0.5f, -0.5f], output);
    }

    [Fact]
    public void PcmConverter_Int24_ToFloat()
    {
        var output = new List<float>();
        PcmConverter.AppendAsFloat([0x00, 0x00, 0x40, 0x00, 0x00, 0xC0], new PcmFormat(false, 24, 1, 48000), 1, output);
        Assert.Equal([0.5f, -0.5f], output);
    }

    [Fact]
    public void Assembler_CutsFixedFrames_AndKeepsTimestamps()
    {
        var assembler = new AudioFrameAssembler(frameSamples: 4, channels: 2, sampleRate: 48000);
        assembler.Pending.AddRange(Enumerable.Range(0, 12).Select(i => (float)i));
        float[] frame = new float[8];
        Assert.True(assembler.TryTakeFrame(frame));
        Assert.Equal([0f, 1, 2, 3, 4, 5, 6, 7], frame);
        Assert.False(assembler.TryTakeFrame(frame)); // 4 floats left = half a frame
        Assert.Equal(4, assembler.SamplesTaken);
        Assert.Equal(4 * 1_000_000L / 48000, assembler.NextTimestampUs);
    }

    [Fact]
    public void Assembler_FillSilence_PadsWholeFramesUpToWallClock()
    {
        var assembler = new AudioFrameAssembler(frameSamples: 960, channels: 2, sampleRate: 48000);
        Assert.Equal(0, assembler.FillSilence(TimeSpan.FromMilliseconds(15))); // less than one frame due
        Assert.Equal(960 * 3, assembler.FillSilence(TimeSpan.FromMilliseconds(70)));
        float[] frame = new float[1920];
        int frames = 0;
        while (assembler.TryTakeFrame(frame))
            frames++;
        Assert.Equal(3, frames);
        Assert.All(frame, s => Assert.Equal(0f, s));
        Assert.Equal(0, assembler.FillSilence(TimeSpan.FromMilliseconds(70))); // caught up
    }

    [Fact]
    public void Assembler_FillSilence_CompletesPartialFrameFirst()
    {
        var assembler = new AudioFrameAssembler(frameSamples: 960, channels: 2, sampleRate: 48000);
        assembler.Pending.AddRange(new float[100 * 2]);
        int added = assembler.FillSilence(TimeSpan.FromMilliseconds(45)); // 2160 due, 100 pending
        Assert.Equal(960 * 2 - 100, added);
        Assert.Equal(960 * 2 * 2, assembler.Pending.Count);
    }

    [Fact]
    public void OpusEncoder_EncodesOneSineFramePerPacket()
    {
        using var encoder = new OpusAudioEncoder(new AudioEncoderOptions());
        Assert.Equal(960, encoder.FrameSamples);
        Assert.Equal("libopus", encoder.EncoderName);
        float[] frame = new float[960 * 2];
        var packets = new List<(int Size, long Pts)>();
        for (int n = 0; n < 10; n++)
        {
            for (int i = 0; i < 960; i++)
                frame[2 * i] = frame[2 * i + 1] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * (n * 960 + i) / 48000f);
            encoder.Encode(frame, n * 20_000L, (data, pts) => packets.Add((data.Length, pts)));
        }
        Assert.InRange(packets.Count, 9, 10); // libopus may hold back the first frame (encoder delay)
        Assert.All(packets, p => Assert.InRange(p.Size, 3, 1275)); // valid Opus packet sizes
    }

    [Fact]
    public void OpusEncoder_RejectsWrongFrameLength()
    {
        using var encoder = new OpusAudioEncoder(new AudioEncoderOptions());
        Assert.Throws<ArgumentException>(() => encoder.Encode(new float[100], 0, (_, _) => { }));
    }
}
