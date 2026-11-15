using System.Diagnostics;
using System.Drawing;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Native;
using FocusRemote.Host.Core.Sources.Enums;
using FocusRemote.Host.Core.Sources.Models;
using FocusRemote.Host.Core.Video.Interfaces;
using FocusRemote.Host.Core.Video.Models;
using FocusRemote.Host.Encoding;
using FocusRemote.Host.Encoding.Models;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models.Control;
using TqkLibrary.WindowCapture.Captures;

namespace FocusRemote.Host.Core.Video;

/// <summary>
/// Captures one source with Windows.Graphics.Capture and encodes it on a dedicated thread at the requested fps.
/// Capture, encoder and all their calls live on that thread (neither is thread-safe).
/// <para>
/// Known limitation (backlog [Cao], docs/Encoder-vi.md): TqkLibrary.WindowCapture 1.0.8 does not Flush after
/// Render, so a frame drawn into the shared surface may not be visible to the encoder device yet. Mitigation
/// here: encode only when Render reports a new capture frame, plus forced/periodic keyframes. A static
/// picture can still come out black or one frame late until the library is fixed upstream.
/// </para>
/// </summary>
public sealed class VideoStreamer : IVideoStreamer
{
    public static readonly TimeSpan KeyframeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _firstFrameTimeout = TimeSpan.FromMilliseconds(500);

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
        using var capture = new WinrtGraphicCapture { MaxFps = _fps };
        if (WinrtGraphicCapture.IsBorderToggleSupported)
            capture.IsShowBorder = false;
        if (WinrtGraphicCapture.IsCaptureCursorToggleSupported)
            capture.IsShowCursor = true;
        bool initialized = _source.Kind == SourceKind.Window ? capture.InitWindow(_source.Handle) : capture.InitMonitor(_source.Handle);
        if (!initialized)
            throw new InvalidOperationException("Không khởi tạo được capture cho nguồn " + _source.Id);

        Stopwatch waitSize = Stopwatch.StartNew();
        while (capture.Size.IsEmpty && waitSize.ElapsedMilliseconds < 3000 && !_stopRequested)
            Thread.Sleep(10);
        if (capture.Size.IsEmpty)
            throw new InvalidOperationException("Capture không trả kích thước nguồn.");

        using var encoder = new HardwareVideoEncoder();
        Size captureSize = capture.Size;
        OpenEncoder(encoder, captureSize);
        int renderedGeneration = int.MinValue;
        WaitFirstFrame(capture, encoder, ref renderedGeneration);

        Stopwatch clock = Stopwatch.StartNew();
        long frameTicks = Stopwatch.Frequency / _fps;
        long nextDue = 0;
        bool forceKeyframe = true;
        TimeSpan lastKeyframe = TimeSpan.MinValue;
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

            if (_source.Kind == SourceKind.Window && !NativeMethods.IsWindow(_source.Handle))
                return "closed";

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

            bool isNewSurface = renderedGeneration != encoder.SurfaceGeneration;
            bool isNewTargetView = false;
            bool rendered = capture.Render(encoder.InputSurface, isNewSurface, ref isNewTargetView);
            renderedGeneration = encoder.SurfaceGeneration;

            TimeSpan elapsed = clock.Elapsed;
            if (_keyframeRequested)
            {
                _keyframeRequested = false;
                forceKeyframe = true;
            }
            bool keyframeDue = forceKeyframe || elapsed - lastKeyframe >= KeyframeInterval;
            if (rendered || keyframeDue)
            {
                long timestampUs = elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
                if (encoder.Encode(keyframeDue, timestampUs, out EncodedPacket packet))
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

    /// <summary>Render(isNewSurface) draws even without a capture frame, so wait for a real frame first.</summary>
    private void WaitFirstFrame(WinrtGraphicCapture capture, HardwareVideoEncoder encoder, ref int renderedGeneration)
    {
        bool isNewTargetView = false;
        capture.Render(encoder.InputSurface, true, ref isNewTargetView);
        renderedGeneration = encoder.SurfaceGeneration;
        Stopwatch wait = Stopwatch.StartNew();
        while (!_stopRequested && wait.Elapsed < _firstFrameTimeout)
        {
            isNewTargetView = false;
            if (capture.Render(encoder.InputSurface, false, ref isNewTargetView))
                return;
            Thread.Sleep(5);
        }
    }

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
