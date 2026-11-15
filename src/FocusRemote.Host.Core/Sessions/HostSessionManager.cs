using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Sessions.Interfaces;
using FocusRemote.Host.Core.Sources.Interfaces;
using FocusRemote.Host.Core.Sources.Models;
using FocusRemote.Host.Core.Video;
using FocusRemote.Host.Core.Video.Interfaces;
using FocusRemote.Host.Core.Video.Models;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Handshake;
using FocusRemote.Protocol.Handshake.Enums;
using FocusRemote.Protocol.Messages.Models.Control;

namespace FocusRemote.Host.Core.Sessions;

/// <summary>
/// Runs the host side of the protocol for every viewer link, whatever the transport (relay or LAN).
/// Enforces one authenticated viewer in total and the brute-force delay shared by all transports.
/// Thread-safe.
/// </summary>
public sealed class HostSessionManager
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<byte[]?> _rootKeyProvider;
    private readonly ISourceCatalog _catalog;
    private readonly IAppLog _log;
    private readonly Func<CaptureSource, SelectSourceMessage, IVideoSink, IVideoStreamer> _streamerFactory;
    private readonly AuthFailureDelayPolicy _failurePolicy;
    private readonly object _lock = new();
    private IViewerLink? _activeLink;

    /// <param name="rootKeyProvider">Current 32-byte root key (null = not configured, viewers are refused).</param>
    /// <param name="streamerFactory">Creates the video streamer; null = <see cref="VideoStreamer"/>.</param>
    public HostSessionManager(
        Func<byte[]?> rootKeyProvider,
        ISourceCatalog catalog,
        IAppLog log,
        Func<CaptureSource, SelectSourceMessage, IVideoSink, IVideoStreamer>? streamerFactory = null,
        AuthFailureDelayPolicy? failurePolicy = null)
    {
        _rootKeyProvider = rootKeyProvider;
        _catalog = catalog;
        _log = log;
        _streamerFactory = streamerFactory ?? ((source, request, sink) => new VideoStreamer(source, request, sink, log));
        _failurePolicy = failurePolicy ?? new AuthFailureDelayPolicy();
    }

    /// <summary>Raised with the viewer origin when a viewer authenticates, and with null when it leaves.</summary>
    public event Action<string?>? ViewerChanged;

    /// <summary>Stream statistics of the active viewer (null when no stream).</summary>
    public event Action<StreamStats?>? StreamStatsChanged;

    /// <summary>True while an authenticated viewer is connected.</summary>
    public bool IsBusy
    {
        get { lock (_lock) return _activeLink is not null; }
    }

    /// <summary>
    /// Handles one viewer link from HelloViewer to disconnect. Returns when the session ends.
    /// Never throws (except cancellation of <paramref name="cancellationToken"/> is swallowed too).
    /// </summary>
    public async Task RunAsync(IViewerLink link, CancellationToken cancellationToken)
    {
        try
        {
            if (IsBusy)
            {
                _log.Info($"Viewer {link.Origin}: refused before handshake, another viewer is connected");
                link.Close();
                return;
            }
            SecureSession? session = await HandshakeAsync(link, cancellationToken);
            if (session is null)
                return;

            lock (_lock)
            {
                if (_activeLink is not null)
                {
                    session.Dispose();
                    session = null;
                }
                else
                {
                    _activeLink = link;
                }
            }
            if (session is null)
            {
                _log.Info($"Viewer {link.Origin}: refused, another viewer is connected");
                link.Close();
                return;
            }

            _log.Info($"Viewer {link.Origin}: authenticated");
            ViewerChanged?.Invoke(link.Origin);
            using var viewer = new ViewerSession(link, session, _catalog, _log, _streamerFactory, stats => StreamStatsChanged?.Invoke(stats));
            try
            {
                await viewer.RunAsync(cancellationToken);
            }
            finally
            {
                lock (_lock)
                    _activeLink = null;
                StreamStatsChanged?.Invoke(null);
                ViewerChanged?.Invoke(null);
                _log.Info($"Viewer {link.Origin}: disconnected");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            link.Close();
        }
        catch (Exception ex)
        {
            _log.Error($"Viewer {link.Origin}: session failed", ex);
            link.Close();
        }
    }

    /// <summary>Returns the established session, or null when the viewer failed/left/timed out (link already closed).</summary>
    private async Task<SecureSession?> HandshakeAsync(IViewerLink link, CancellationToken cancellationToken)
    {
        byte[]? rootKey = _rootKeyProvider();
        if (rootKey is null)
        {
            _log.Warn($"Viewer {link.Origin}: refused, host has no password configured");
            link.Close();
            return null;
        }

        var handshake = new HostHandshake(rootKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        try
        {
            byte[] helloHost;
            while (true)
            {
                byte[]? message = await ReadAsync(link, timeout.Token);
                if (message is null)
                    return null;
                // Sealed frames before the handshake completes are ignored (protocol §4).
                if (handshake.TryHandleHelloViewer(message, out helloHost))
                    break;
            }

            // Brute-force throttle: wait out the delay from previous failures before answering (protocol §4.5).
            TimeSpan delay = _failurePolicy.GetRemainingDelay();
            if (delay > TimeSpan.Zero)
            {
                _log.Info($"Viewer {link.Origin}: delaying handshake {delay.TotalSeconds:F1}s after failed attempts");
                await Task.Delay(delay, cancellationToken);
            }
            await link.SendAsync(helloHost, timeout.Token);

            while (true)
            {
                byte[]? message = await ReadAsync(link, timeout.Token);
                if (message is null)
                    return null;
                switch (handshake.HandleProbe(message, out byte[]? authFail))
                {
                    case ProbeResult.Accepted:
                        _failurePolicy.RegisterSuccess();
                        return handshake.Session;
                    case ProbeResult.Rejected:
                        TimeSpan next = _failurePolicy.RegisterFailure();
                        _log.Warn($"Viewer {link.Origin}: wrong password (next attempt delayed {next.TotalSeconds:F0}s)");
                        await link.SendAsync(authFail!, timeout.Token);
                        link.Close();
                        return null;
                    default:
                        if (message.Length > 0 && message[0] == (byte)FrameKind.HelloViewer)
                            _log.Info($"Viewer {link.Origin}: duplicate HelloViewer ignored");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Info($"Viewer {link.Origin}: handshake timed out");
            link.Close();
            return null;
        }
    }

    private static async Task<byte[]?> ReadAsync(IViewerLink link, CancellationToken cancellationToken)
    {
        while (await link.Incoming.WaitToReadAsync(cancellationToken))
        {
            if (link.Incoming.TryRead(out byte[]? message))
                return message;
        }
        return null;
    }
}
