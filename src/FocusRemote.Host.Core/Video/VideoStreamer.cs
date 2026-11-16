using System.Diagnostics;
using System.Drawing;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Native;
using FocusRemote.Host.Core.Sources.Enums;
using FocusRemote.Host.Core.Sources.Models;
using FocusRemote.Host.Core.Video.Interfaces;
using FocusRemote.Host.Core.Video.Models;
using FocusRemote.Host.Encoding;
using FocusRemote.Host.Encoding.Enums;
using FocusRemote.Host.Encoding.Models;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models.Control;

namespace FocusRemote.Host.Core.Video;

/// <summary>
/// Captures one source (Windows.Graphics.Capture inside the encoder DLL, on the encoder's D3D11 device) and encodes
/// it on a dedicated thread at the requested fps. Capture, encoder and all their calls live on that thread.
/// </summary>
public sealed class VideoStreamer : IVideoStreamer
{
    public static readonly TimeSpan KeyframeInterval = TimeSpan.FromSeconds(5);
    /// <summary>While the picture is static the last frame is re-encoded at this interval (5 fps).</summary>
    public static readonly TimeSpan StaticRepeatInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan _firstFrameTimeout = TimeSpan.FromSeconds(3);

    private readonly CaptureSource _source;
    private readonly SelectSourceMessage _request;
    private readonly IVideoSink _sink;
    private readonly IAppLog _log;
    private readonly Thread _thread;
    private readonly int _fps;
    private readonly int _bitrateKbps;
    private volatile bool _stopRequested;
    private volatile bool _keyframeRequested;

    public VideoStreamer(CaptureSource source, SelectSourceMessage request, IVideoSink sink, IAppLog log)
    {
        _source = source;
        _request = request;
        _sink = sink;
        _log = log;
        _fps = request.MaxFps <= 0 ? 30 : Math.Clamp(request.MaxFps, 1, 60);
        _bitrateKbps = request.BitrateKbps <= 0 ? 4000 : Math.Clamp(request.BitrateKbps, 200, 50_000);
        _thread = new Thread(Run) { IsBackground = true, Name = "FocusRemote video " + source.Id };
    }

    public CaptureSource Source => _source;

    public void Start() => _thread.Start();

    /// <summary>Next encoded frame will be a keyframe (viewer RequestKeyframe).</summary>
    public void RequestKeyframe() => _keyframeRequested = true;

    /// <summary>Stops the thread and waits for it. Does not report StreamStopped.</summary>
    public void Dispose()
    {
        _stopRequested = true;
        if (_thread.IsAlive && _thread != Thread.CurrentThread)
            _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        try
        {
            string? stopReason = RunCore();
            if (stopReason is not null && !_stopRequested)
                _sink.OnStreamStopped(stopReason, null);
        }
        catch (Exception ex)
        {
            _log.Error($"Stream {_source.Id} failed", ex);
            if (!_stopRequested)
                _sink.OnStreamStopped("capture_failed", new ErrorMessage { Code = "capture_failed", Message = ex.Message });
        }
    }

    /// <summary>Returns the StreamStopped reason, or null when stopped by <see cref="Dispose"/>.</summary>
    private string? RunCore()
    {
        using var encoder = new HardwareVideoEncoder();
        CaptureTargetKind kind = _source.Kind == SourceKind.Window ? CaptureTargetKind.Window : CaptureTargetKind.Monitor;
        using var capture = new ScreenCapture(encoder, kind, _source.Handle, showCursor: true);

        // Encode needs a frame; a static source still delivers its first frame right after start, but a minimized
        // window delivers none until restored, so keep waiting (no StreamStarted yet) instead of failing.
        Stopwatch waitFrame = Stopwatch.StartNew();
        bool waitLogged = false;
        while (!capture.Poll())
        {
            if (_stopRequested)
                return null;
            if (capture.IsClosed || (_source.Kind == SourceKind.Window && !NativeMethods.IsWindow(_source.Handle)))
                return "closed";
            if (!waitLogged && waitFrame.Elapsed >= _firstFrameTimeout)
            {
                waitLogged = true;
                _log.Info($"Stream {_source.Id}: no frame after {_firstFrameTimeout.TotalSeconds:0} s (minimized window?), still waiting");
            }
            Thread.Sleep(20);
        }

        Size captureSize = capture.Size;
        OpenEncoder(encoder, captureSize);

        Stopwatch clock = Stopwatch.StartNew();
        long frameTicks = Stopwatch.Frequency / _fps;
        long nextDue = 0;
        bool forceKeyframe = true;
        TimeSpan lastKeyframe = TimeSpan.MinValue;
        TimeSpan lastEncode = TimeSpan.Zero; // first tick encodes anyway (forceKeyframe)
        StatsWindow stats = new(clock.Elapsed);

        while (!_stopRequested)
        {
            long now = clock.ElapsedTicks;
            if (now < nextDue)
            {
                Thread.Sleep(TimeSpan.FromTicks(Math.Max(1, (nextDue - now) * TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
                continue;
            }
            nextDue = Math.Max(nextDue + frameTicks, now);

            if (capture.IsClosed || (_source.Kind == SourceKind.Window && !NativeMethods.IsWindow(_source.Handle)))
                return "closed";

            bool newFrame = capture.Poll();
            Size size = capture.Size;
            if (!size.IsEmpty && size != captureSize)
            {
                _log.Info($"Stream {_source.Id}: source size {captureSize.Width}x{captureSize.Height} -> {size.Width}x{size.Height}, reopening encoder");
                captureSize = size;
                OpenEncoder(encoder, captureSize);
                forceKeyframe = true;
            }

            if (_sink.IsCongested)
            {
                stats.Dropped++;
                forceKeyframe = true;
                PublishStats(stats, clock.Elapsed, encoder);
                continue;
            }

            TimeSpan elapsed = clock.Elapsed;
            if (_keyframeRequested)
            {
                _keyframeRequested = false;
                forceKeyframe = true;
            }
            bool keyframeDue = forceKeyframe || elapsed - lastKeyframe >= KeyframeInterval;
            // WinRT capture only delivers frames on change: repeat the last picture at a low rate while static
            // so a viewer still gets a steady (cheap) stream.
            bool repeatDue = elapsed - lastEncode >= StaticRepeatInterval;
            if (newFrame || keyframeDue || repeatDue)
            {
                long timestampUs = elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
                lastEncode = elapsed;
                if (encoder.Encode(capture, keyframeDue, timestampUs, out EncodedPacket packet))
                {
                    _sink.OnFrame(VideoMessages.BuildFrame(packet.IsKeyframe, (ulong)timestampUs, packet.Data));
                    if (packet.IsKeyframe)
                    {
                        lastKeyframe = elapsed;
                        forceKeyframe = false;
                    }
                    stats.Frames++;
                    stats.Bytes += packet.Data.Length;
                }
            }
            PublishStats(stats, elapsed, encoder);
        }
        return null;
    }

    private void OpenEncoder(HardwareVideoEncoder encoder, Size captureSize)
    {
        (int width, int height) = ScaleCalculator.Fit(captureSize.Width, captureSize.Height, _request.MaxWidth, _request.MaxHeight);
        encoder.Open(new VideoEncoderOptions
        {
            Width = width,
            Height = height,
            Fps = _fps,
            BitrateKbps = _bitrateKbps,
            // Keyframes are driven here (time based) because frames are skipped while the picture is static.
            GopSeconds = 0,
        });
        _encodeWidth = width;
        _encodeHeight = height;
        _log.Info($"Stream {_source.Id}: encoder {encoder.EncoderName} (zero-copy {encoder.IsZeroCopy}) {width}x{height} @{_fps} fps {_bitrateKbps} kbps");
        _sink.OnStreamStarted(new StreamStartedMessage
        {
            SourceId = _source.Id,
            Codec = "h264",
            Width = width,
            Height = height,
            SourceWidth = captureSize.Width,
            SourceHeight = captureSize.Height,
        });
    }

    private int _encodeWidth;
    private int _encodeHeight;

    private void PublishStats(StatsWindow stats, TimeSpan now, HardwareVideoEncoder encoder)
    {
        TimeSpan span = now - stats.Start;
        if (span < TimeSpan.FromSeconds(1))
            return;
        _sink.OnStats(new StreamStats(
            _source.Id,
            encoder.EncoderName,
            _encodeWidth,
            _encodeHeight,
            stats.Frames / span.TotalSeconds,
            stats.Bytes * 8 / 1000.0 / span.TotalSeconds,
            stats.Dropped));
        stats.Reset(now);
    }

    private sealed class StatsWindow(TimeSpan start)
    {
        public TimeSpan Start { get; private set; } = start;
        public int Frames { get; set; }
        public long Bytes { get; set; }
        public int Dropped { get; set; }

        public void Reset(TimeSpan start)
        {
            Start = start;
            Frames = 0;
            Bytes = 0;
            Dropped = 0;
        }
    }
}
