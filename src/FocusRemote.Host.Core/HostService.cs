using System.Net;
using System.Security.Cryptography.X509Certificates;
using FocusRemote.Host.Core.Lan;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Core.Relay;
using FocusRemote.Host.Core.Relay.Enums;
using FocusRemote.Host.Core.Sessions;
using FocusRemote.Host.Core.Sources;
using FocusRemote.Host.Core.Settings.Models;

namespace FocusRemote.Host.Core;

/// <summary>
/// Runs the host with the given settings: one <see cref="HostSessionManager"/> shared by the external relay
/// client and the LAN server (both may run at once; still one viewer in total). Raises <see cref="StatusChanged"/>
/// from background threads. Not thread-safe for Start/Stop: call them from one (UI) thread.
/// </summary>
public sealed class HostService : IAsyncDisposable
{
    private readonly IAppLog _log;
    private readonly string _webRoot;
    private readonly object _statusLock = new();
    private HostStatus _status = new();
    private HostSessionManager? _manager;
    private RelayHostClient? _relay;
    private LanServer? _lan;
    private X509Certificate2? _certificate;

    /// <param name="webRoot">Folder with the WebViewer files served in LAN mode.</param>
    public HostService(IAppLog log, string webRoot)
    {
        _log = log;
        _webRoot = webRoot;
    }

    public event Action<HostStatus>? StatusChanged;

    public HostStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    public async Task StartAsync(HostSettings settings, byte[] rootKey)
    {
        await StopAsync();
        string id = settings.Id;
        byte[] key = rootKey.ToArray();
        var manager = new HostSessionManager(() => key, new SourceCatalog(), _log);
        manager.ViewerChanged += viewer => Update(s => s with { Viewer = viewer, Stream = viewer is null ? null : s.Stream });
        manager.StreamStatsChanged += stats => Update(s => s with { Stream = stats });
        _manager = manager;

        bool relayEnabled = !string.IsNullOrWhiteSpace(settings.RelayUrl);
        Update(_ => new HostStatus { IsRunning = true, RelayEnabled = relayEnabled, RelayState = RelayState.Stopped, LanPort = settings.LanPort });

        if (relayEnabled)
        {
            try
            {
                var relay = new RelayHostClient(settings.RelayUrl, id, manager, _log);
                relay.StateChanged += (state, detail) => Update(s => s with { RelayState = state, RelayDetail = detail });
                _relay = relay;
                relay.Start();
            }
            catch (UriFormatException ex)
            {
                Update(s => s with { RelayState = RelayState.Stopped, RelayDetail = ex.Message });
            }
        }

        if (settings.LanEnabled)
        {
            try
            {
                IReadOnlyList<IPAddress> addresses = LanAddressProvider.GetLocalIPv4Addresses();
                _certificate = new LanCertificateManager(AppPaths.LanCertificateFile, _log).GetOrCreate(Environment.MachineName, addresses);
                var lan = new LanServer(settings.LanPort, _certificate, _webRoot, () => id, manager, _log);
                await lan.StartAsync();
                _lan = lan;
                IReadOnlyList<string> urls = LanAddressProvider.BuildUrls(addresses, Environment.MachineName, lan.BoundPort);
                Update(s => s with { LanRunning = true, LanError = null, LanUrls = urls, LanPort = lan.BoundPort });
            }
            catch (Exception ex)
            {
                _log.Error("LAN server failed to start", ex);
                Update(s => s with { LanRunning = false, LanError = ex.Message });
            }
        }
    }

    public async Task StopAsync()
    {
        if (_relay is not null)
        {
            await _relay.DisposeAsync();
            _relay = null;
        }
        if (_lan is not null)
        {
            await _lan.DisposeAsync();
            _lan = null;
        }
        _certificate?.Dispose();
        _certificate = null;
        _manager = null;
        Update(_ => new HostStatus());
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void Update(Func<HostStatus, HostStatus> change)
    {
        HostStatus snapshot;
        lock (_statusLock)
        {
            _status = change(_status);
            snapshot = _status;
        }
        try
        {
            StatusChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _log.Error("StatusChanged handler failed", ex);
        }
    }
}
