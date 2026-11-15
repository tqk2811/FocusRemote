using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Relay.Enums;
using FocusRemote.Host.Core.Sessions;
using FocusRemote.Protocol;

namespace FocusRemote.Host.Core.Relay;

/// <summary>
/// Keeps the host registered on the external relay (<c>wss://relay/ws/host/{id}</c>, protocol §1):
/// reconnects with exponential backoff (1s → 30s), WebSocket keep-alive pings, and turns
/// <c>peer-joined</c>/<c>peer-left</c> into <see cref="RelayViewerLink"/> sessions on the shared manager.
/// </summary>
public sealed class RelayHostClient : IAsyncDisposable
{
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _minBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _maxBackoff = TimeSpan.FromSeconds(30);

    private readonly Uri _hostUri;
    private readonly HostSessionManager _manager;
    private readonly IAppLog _log;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connect;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <param name="connect">Opens the WebSocket; null = <see cref="ClientWebSocket"/> with keep-alive. Tests inject a TestServer client.</param>
    public RelayHostClient(string relayUrl, string id, HostSessionManager manager, IAppLog log, Func<Uri, CancellationToken, Task<WebSocket>>? connect = null)
    {
        _hostUri = BuildHostUri(relayUrl, id);
        _manager = manager;
        _log = log;
        _connect = connect ?? ConnectDefaultAsync;
    }

    public Uri HostUri => _hostUri;

    /// <summary>State changes with an optional detail message (Vietnamese, for the UI).</summary>
    public event Action<RelayState, string?>? StateChanged;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
        StateChanged?.Invoke(RelayState.Stopped, null);
    }

    /// <summary>
    /// <c>https://x/sub</c> → <c>wss://x/sub/ws/host/{id}</c> (http → ws). Throws <see cref="UriFormatException"/>
    /// for anything that is not an absolute http/https/ws/wss URL.
    /// </summary>
    public static Uri BuildHostUri(string relayUrl, string id)
    {
        if (!Uri.TryCreate(relayUrl?.Trim(), UriKind.Absolute, out Uri? baseUri))
            throw new UriFormatException("Relay URL không hợp lệ.");
        string scheme = baseUri.Scheme.ToLowerInvariant() switch
        {
            "https" or "wss" => "wss",
            "http" or "ws" => "ws",
            _ => throw new UriFormatException("Relay URL phải là http(s) hoặc ws(s)."),
        };
        var builder = new UriBuilder(baseUri) { Scheme = scheme, Query = string.Empty, Fragment = string.Empty };
        if (builder.Uri.IsDefaultPort)
            builder.Port = -1;
        string path = builder.Path.EndsWith('/') ? builder.Path : builder.Path + "/";
        builder.Path = path + "ws/host/" + Uri.EscapeDataString(id);
        return builder.Uri;
    }

    private static async Task<WebSocket> ConnectDefaultAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = KeepAliveInterval;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(uri, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan backoff = _minBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            StateChanged?.Invoke(RelayState.Connecting, _hostUri.GetLeftPart(UriPartial.Authority));
            string detail;
            try
            {
                using WebSocket socket = await _connect(_hostUri, cancellationToken);
                (bool registered, WebSocketCloseStatus? status) = await RunConnectionAsync(socket, cancellationToken);
                if (registered)
                    backoff = _minBackoff;
                detail = (int?)status switch
                {
                    4000 => "Relay từ chối: Id không hợp lệ",
                    4009 => "Id đang được một host khác dùng trên relay",
                    4029 => "Relay báo vượt rate limit",
                    _ => "Mất kết nối relay",
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn($"Relay {_hostUri}: {ex.GetType().Name}: {ex.Message}");
                detail = "Không kết nối được relay: " + ex.Message;
            }

            StateChanged?.Invoke(RelayState.WaitingRetry, $"{detail} (thử lại sau {backoff.TotalSeconds:F0}s)");
            try
            {
                await Task.Delay(backoff, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(_maxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    private async Task<(bool Registered, WebSocketCloseStatus? CloseStatus)> RunConnectionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var sendLock = new SemaphoreSlim(1, 1);
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task SendAsync(byte[] data, CancellationToken token)
        {
            await sendLock.WaitAsync(token);
            try
            {
                await socket.SendAsync(data, WebSocketMessageType.Binary, true, token);
            }
            finally
            {
                sendLock.Release();
            }
        }

        bool registered = false;
        RelayViewerLink? viewer = null;
        byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                (WebSocketMessageType type, byte[]? data) = await ReceiveMessageAsync(socket, chunk, cancellationToken);
                if (type == WebSocketMessageType.Close || data is null)
                    return (registered, socket.CloseStatus);

                if (type == WebSocketMessageType.Binary)
                {
                    viewer?.Post(data);
                    continue;
                }

                switch (ReadStatusType(data))
                {
                    case "registered":
                        registered = true;
                        _log.Info($"Relay {_hostUri}: registered");
                        StateChanged?.Invoke(RelayState.Registered, _hostUri.GetLeftPart(UriPartial.Authority));
                        break;
                    case "peer-joined":
                        viewer?.Close();
                        viewer = new RelayViewerLink(SendAsync);
                        _log.Info($"Relay {_hostUri}: viewer joined");
                        RelayViewerLink joined = viewer;
                        _ = Task.Run(() => _manager.RunAsync(joined, connectionCts.Token), CancellationToken.None);
                        break;
                    case "peer-left":
                        _log.Info($"Relay {_hostUri}: viewer left");
                        viewer?.Close();
                        viewer = null;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException && !cancellationToken.IsCancellationRequested)
        {
            _log.Warn($"Relay {_hostUri}: connection lost ({ex.Message})");
            return (registered, null);
        }
        finally
        {
            viewer?.Close();
            connectionCts.Cancel();
            ArrayPool<byte>.Shared.Return(chunk);
            if (socket.State == WebSocketState.Open && cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token);
                }
                catch (Exception closeEx) when (closeEx is WebSocketException or OperationCanceledException)
                {
                }
            }
        }
    }

    private static async Task<(WebSocketMessageType Type, byte[]? Data)> ReceiveMessageAsync(WebSocket socket, byte[] chunk, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(chunk, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, null);
            if (buffer.Length + result.Count > ProtocolConstants.MaxMessageSize)
                throw new WebSocketException(WebSocketError.Faulted, "Message too big");
            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage)
                return (result.MessageType, buffer.ToArray());
        }
    }

    private static string? ReadStatusType(byte[] json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("type", out JsonElement type) ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
