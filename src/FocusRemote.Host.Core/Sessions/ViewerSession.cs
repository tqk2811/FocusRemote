using System.Reflection;
using FocusRemote.Host.Core.Input;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Core.Sessions.Interfaces;
using FocusRemote.Host.Core.Sources.Enums;
using FocusRemote.Host.Core.Sources.Interfaces;
using FocusRemote.Host.Core.Sources.Models;
using FocusRemote.Host.Core.Video;
using FocusRemote.Host.Core.Video.Interfaces;
using FocusRemote.Host.Core.Video.Models;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models.Control;
using FocusRemote.Protocol.Messages.Models.Input;

namespace FocusRemote.Host.Core.Sessions;

/// <summary>
/// An authenticated viewer: one receive loop (this task) dispatches Control/Input, one writer task sends.
/// Owns the <see cref="SecureSession"/> and the running <see cref="VideoStreamer"/>.
/// </summary>
internal sealed class ViewerSession : IVideoSink, IDisposable
{
    private readonly IViewerLink _link;
    private readonly SecureSession _session;
    private readonly ISourceCatalog _catalog;
    private readonly IAppLog _log;
    private readonly Func<CaptureSource, SelectSourceMessage, IVideoSink, IVideoStreamer> _streamerFactory;
    private readonly Action<StreamStats?> _statsChanged;
    private readonly OutboundQueue _outbound = new();
    private readonly InputInjector _input;
    private readonly object _streamLock = new();
    private IVideoStreamer? _streamer;

    public ViewerSession(
        IViewerLink link,
        SecureSession session,
        ISourceCatalog catalog,
        IAppLog log,
        Func<CaptureSource, SelectSourceMessage, IVideoSink, IVideoStreamer> streamerFactory,
        Action<StreamStats?> statsChanged)
    {
        _link = link;
        _session = session;
        _catalog = catalog;
        _log = log;
        _streamerFactory = streamerFactory;
        _statsChanged = statsChanged;
        _input = new InputInjector(log);
    }

    public bool IsCongested => _outbound.IsCongested;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var writerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task writer = Task.Run(() => _outbound.RunWriterAsync(_session, _link, writerCts.Token), CancellationToken.None);
        try
        {
            SendControl(ControlType.AuthOk, new AuthOkMessage { HostName = Environment.MachineName, Version = HostVersion });
            SendSourceList();

            await foreach (byte[] message in _link.Incoming.ReadAllAsync(cancellationToken))
            {
                if (writer.IsCompleted)
                    break;
                if (message.Length == 0 || message[0] != (byte)FrameKind.Sealed)
                    continue;
                if (!_session.TryOpen(message, out byte channel, out byte type, out byte[] payload))
                {
                    _log.Warn($"Viewer {_link.Origin}: Sealed frame rejected (auth/replay), closing session");
                    break;
                }
                Dispatch(channel, type, payload);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            StopStreamer();
            _input.ReleaseAll();
            _outbound.Complete();
            try
            {
                await writer.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Info($"Viewer {_link.Origin}: writer ended ({ex.GetType().Name}: {ex.Message})");
            }
            writerCts.Cancel();
            _link.Close();
        }
    }

    private static string HostVersion => typeof(ViewerSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

    private void Dispatch(byte channel, byte type, byte[] payload)
    {
        switch ((Channel)channel)
        {
            case Channel.Control:
                HandleControl((ControlType)type, payload);
                break;
            case Channel.Input:
                if (InputMessages.TryParse(type, payload, out InputMessage? input))
                    HandleInput(input!);
                break;
            default:
                // Video/Audio from the viewer and reserved channels are ignored (protocol §5).
                break;
        }
    }

    private void HandleControl(ControlType type, byte[] payload)
    {
        switch (type)
        {
            case ControlType.Capabilities:
                if (ControlMessages.TryDeserializeJson(payload, out CapabilitiesMessage? caps) && caps!.VideoCodecs is not null)
                    _log.Info($"Viewer {_link.Origin}: codecs {string.Join(",", caps.VideoCodecs)}");
                break;
            case ControlType.ListSources:
                SendSourceList();
                break;
            case ControlType.SelectSource:
                if (ControlMessages.TryDeserializeJson(payload, out SelectSourceMessage? select) && select!.Id is not null)
                    StartStream(select);
                break;
            case ControlType.StopStream:
            {
                string? sourceId = StopStreamer();
                if (sourceId is not null)
                    SendControl(ControlType.StreamStopped, new StreamStoppedMessage { SourceId = sourceId, Reason = "stopped" });
                break;
            }
            case ControlType.RequestKeyframe:
                lock (_streamLock)
                    _streamer?.RequestKeyframe();
                break;
            case ControlType.Ping:
                if (ControlMessages.TryParsePing(payload, out _))
                    _outbound.Enqueue(Channel.Control, (byte)ControlType.Pong, payload);
                break;
            default:
                break;
        }
    }

    private void HandleInput(InputMessage input)
    {
        CaptureSource? source;
        lock (_streamLock)
            source = _streamer?.Source;
        if (source is null)
        {
            if (input is ReleaseAllInput)
                _input.ReleaseAll();
            return;
        }
        PixelRect? bounds = _catalog.GetCurrentBounds(source);
        if (bounds is not { IsEmpty: false })
            return;
        IntPtr foreground = source.Kind == SourceKind.Window ? source.Handle : IntPtr.Zero;
        _input.Handle(input, bounds.Value, foreground);
    }

    private void StartStream(SelectSourceMessage select)
    {
        StopStreamer();
        CaptureSource? source = _catalog.Find(select.Id);
        if (source is null)
        {
            SendControl(ControlType.Error, new ErrorMessage { Code = "source_not_found", Message = "Nguồn không còn tồn tại: " + select.Id });
            SendSourceList();
            return;
        }
        _log.Info($"Viewer {_link.Origin}: select {source.Id} '{source.Title}' max {select.MaxWidth}x{select.MaxHeight} @{select.MaxFps} {select.BitrateKbps} kbps");
        IVideoStreamer streamer = _streamerFactory(source, select, this);
        lock (_streamLock)
            _streamer = streamer;
        streamer.Start();
    }

    /// <summary>Stops the current stream (if any) and returns its source id.</summary>
    private string? StopStreamer()
    {
        IVideoStreamer? streamer;
        lock (_streamLock)
        {
            streamer = _streamer;
            _streamer = null;
        }
        if (streamer is null)
            return null;
        streamer.Dispose();
        _input.ReleaseAll();
        _statsChanged(null);
        return streamer.Source.Id;
    }

    private void SendSourceList()
    {
        IReadOnlyList<SourceInfo> sources = _catalog.GetSources().Select(s => s.ToSourceInfo()).ToList();
        SendControl(ControlType.SourceList, new SourceListMessage { Sources = sources });
    }

    private void SendControl<T>(ControlType type, T message)
        => _outbound.Enqueue(Channel.Control, (byte)type, ControlMessages.SerializeJson(message));

    // IVideoSink (called on the streamer thread).

    void IVideoSink.OnStreamStarted(StreamStartedMessage message) => SendControl(ControlType.StreamStarted, message);

    void IVideoSink.OnFrame(byte[] payload) => _outbound.EnqueueVideoFrame(payload);

    void IVideoSink.OnStreamStopped(string reason, ErrorMessage? error)
    {
        string sourceId;
        lock (_streamLock)
        {
            if (_streamer is null)
                return;
            sourceId = _streamer.Source.Id;
            _streamer = null; // the thread is ending by itself; no Join needed
        }
        _statsChanged(null);
        if (error is not null)
            SendControl(ControlType.Error, error);
        SendControl(ControlType.StreamStopped, new StreamStoppedMessage { SourceId = sourceId, Reason = reason });
    }

    void IVideoSink.OnStats(StreamStats stats) => _statsChanged(stats);

    public void Dispose() => _session.Dispose();
}
