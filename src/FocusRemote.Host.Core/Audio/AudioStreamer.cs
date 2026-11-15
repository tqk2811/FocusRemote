using System.Diagnostics;
using FocusRemote.Host.Core.Audio.Interfaces;
using FocusRemote.Host.Core.Audio.Models;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Native;
using FocusRemote.Host.Core.Sources.Enums;
using FocusRemote.Host.Core.Sources.Models;
using FocusRemote.Host.Encoding;
using FocusRemote.Host.Encoding.Models;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models;
using TqkLibrary.AudioCapture;
using TqkLibrary.AudioCapture.Enums;
using TqkLibrary.AudioCapture.Streams;

namespace FocusRemote.Host.Core.Audio;

/// <summary>
/// Captures the audio of one source with WASAPI loopback (TqkLibrary.AudioCapture) and encodes Opus 48 kHz stereo,
/// 20 ms frames, on a dedicated thread. Window: process loopback of the window's process tree; monitor: loopback of
/// the default render endpoint (whole system). WASAPI converts to 48 kHz float (AUTOCONVERTPCM), so no resampler.
/// Failures only stop the audio (logged), never the video stream.
/// </summary>
public sealed class AudioStreamer : IAudioStreamer
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BitrateKbps = 96;
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(5);
    /// <summary>No captured data for this long = nothing is playing: pad with silence (see <see cref="AudioFrameAssembler"/>).</summary>
    private static readonly TimeSpan _idleBeforeSilence = TimeSpan.FromMilliseconds(100);

    private readonly CaptureSource _source;
    private readonly IAudioSink _sink;
    private readonly IAppLog _log;
    private readonly Thread _thread;
    private volatile bool _stopRequested;

    public AudioStreamer(CaptureSource source, IAudioSink sink, IAppLog log)
    {
        _source = source;
        _sink = sink;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "FocusRemote audio " + source.Id };
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _stopRequested = true;
        if (_thread.IsAlive && _thread != Thread.CurrentThread)
            _thread.Join(TimeSpan.FromSeconds(3));
    }

    private void Run()
    {
        try
        {
            RunCore();
        }
        catch (Exception ex)
        {
            _log.Warn($"Audio {_source.Id}: stopped ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private void RunCore()
    {
        using var encoder = new OpusAudioEncoder(new AudioEncoderOptions { SampleRate = SampleRate, Channels = Channels, BitrateKbps = BitrateKbps, FrameDurationMs = 20 });
        using AudioCaptureStream capture = OpenCapture();
        var format = new PcmFormat(capture.FormatTag == AudioFormatTag.IEEE_FLOAT, capture.BitsPerSample, capture.Channels, capture.SampleRate);
        _log.Info($"Audio {_source.Id}: {encoder.EncoderName} {SampleRate} Hz {Channels} ch, capture {capture.Format}");
        if (format.SampleRate != SampleRate)
            throw new InvalidOperationException($"capture delivered {format.SampleRate} Hz instead of {SampleRate} Hz");

        _sink.OnAudioConfig(new AudioConfigMessage { Codec = "opus", SampleRate = SampleRate, Channels = Channels });

        var assembler = new AudioFrameAssembler(encoder.FrameSamples, Channels, SampleRate);
        float[] frame = new float[encoder.FrameSamples * Channels];
        byte[] buffer = new byte[SampleRate * format.Channels * Math.Max(1, format.BitsPerSample / 8)]; // 1 s: WASAPI drops what does not fit
        var clock = Stopwatch.StartNew();
        TimeSpan lastData = TimeSpan.Zero;
        while (!_stopRequested)
        {
            int read;
            bool gotData = false;
            while ((read = capture.Read(buffer, 0, buffer.Length)) > 0)
            {
                PcmConverter.AppendAsFloat(buffer.AsSpan(0, read), format, Channels, assembler.Pending);
                gotData = true;
            }
            if (gotData)
                lastData = clock.Elapsed;
            else if (clock.Elapsed - lastData >= _idleBeforeSilence)
                assembler.FillSilence(clock.Elapsed);

            while (assembler.TryTakeFrame(frame))
            {
                long timestampUs = assembler.NextTimestampUs - 1_000_000L * encoder.FrameSamples / SampleRate;
                encoder.Encode(frame, timestampUs, (data, pts) => _sink.OnAudioPacket(AudioMessages.BuildPacket((ulong)Math.Max(0, pts), data)));
            }
            Thread.Sleep(_pollInterval);
        }
    }

    private AudioCaptureStream OpenCapture()
    {
        if (_source.Kind == SourceKind.Window)
        {
            NativeMethods.GetWindowThreadProcessId(_source.Handle, out uint processId);
            if (processId == 0)
                throw new InvalidOperationException("window has no process");
            return OpenWithFallback(tag => WindowAudioCapture.CaptureProcess((int)processId, tag, Channels, SampleRate, tag == AudioFormatTag.IEEE_FLOAT ? 32 : 16));
        }
        return OpenWithFallback(tag => WindowAudioCapture.CaptureEndpoint((string?)null, tag, Channels, SampleRate, tag == AudioFormatTag.IEEE_FLOAT ? 32 : 16));
    }

    /// <summary>Float first (no quantization); 16-bit PCM if the device refuses float.</summary>
    private AudioCaptureStream OpenWithFallback(Func<AudioFormatTag, AudioCaptureStream> open)
    {
        try
        {
            return open(AudioFormatTag.IEEE_FLOAT);
        }
        catch (InvalidOperationException ex)
        {
            _log.Info($"Audio {_source.Id}: float capture failed ({ex.Message}), trying 16-bit PCM");
            return open(AudioFormatTag.PCM);
        }
    }
}
