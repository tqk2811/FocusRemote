using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FocusRemote.Relay.Relay;

/// <summary>Pairs hosts with viewers and forwards binary messages between them (see docs/Protocol-vi.md §1).</summary>
internal sealed partial class RelayService
{
    public const WebSocketCloseStatus InvalidId = (WebSocketCloseStatus)4000;
    public const WebSocketCloseStatus NoHost = (WebSocketCloseStatus)4004;
    public const WebSocketCloseStatus Conflict = (WebSocketCloseStatus)4009;
    public const WebSocketCloseStatus RateLimited = (WebSocketCloseStatus)4029;

    private readonly ConcurrentDictionary<string, HostSession> _hosts = new(StringComparer.Ordinal);
    private readonly RelayOptions _options;
    private readonly ILogger<RelayService> _logger;

    public RelayService(IOptions<RelayOptions> options, ILogger<RelayService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{3,64}$")]
    private static partial Regex IdRegex();

    public static bool IsValidId(string? id) => id is not null && IdRegex().IsMatch(id);

    public async Task HandleHostAsync(HttpContext context, string id)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "?";
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!IsValidId(id))
        {
            _logger.LogInformation("Host rejected: invalid id from {Ip}", ip);
            await RejectAsync(socket, InvalidId);
            return;
        }

        var host = new PeerConnection(socket, _options.QueueCapacity);
        var session = new HostSession(id, host);
        if (!_hosts.TryAdd(id, session))
        {
            _logger.LogInformation("Host rejected: id {Id} already registered, from {Ip}", id, ip);
            await host.CloseAsync(Conflict);
            await host.CompleteAsync();
            return;
        }

        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure;
        _logger.LogInformation("Host connected: id {Id}, ip {Ip}", id, ip);
        try
        {
            await host.SendStatusAsync("registered");
            closeStatus = await ReceiveLoopAsync(socket, () => session.Viewer, context.RequestAborted);
        }
        finally
        {
            _hosts.TryRemove(new KeyValuePair<string, HostSession>(id, session));
            PeerConnection? viewer = session.MarkClosed();
            if (viewer is not null)
            {
                await viewer.SendStatusAsync("peer-left");
                await viewer.CloseAsync(WebSocketCloseStatus.NormalClosure);
            }
            await host.CloseAsync(closeStatus);
            await host.CompleteAsync();
            _logger.LogInformation("Host disconnected: id {Id}, ip {Ip}", id, ip);
        }
    }

    public async Task HandleViewerAsync(HttpContext context, string id)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "?";
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!IsValidId(id))
        {
            _logger.LogInformation("Viewer rejected: invalid id from {Ip}", ip);
            await RejectAsync(socket, InvalidId);
            return;
        }
        if (!_hosts.TryGetValue(id, out HostSession? session))
        {
            _logger.LogInformation("Viewer rejected: no host {Id}, from {Ip}", id, ip);
            await RejectAsync(socket, NoHost);
            return;
        }

        var viewer = new PeerConnection(socket, _options.QueueCapacity);
        if (!session.TryAttachViewer(viewer))
        {
            _logger.LogInformation("Viewer rejected: host {Id} busy, from {Ip}", id, ip);
            await viewer.CloseAsync(Conflict);
            await viewer.CompleteAsync();
            return;
        }

        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure;
        _logger.LogInformation("Viewer connected: id {Id}, ip {Ip}", id, ip);
        try
        {
            await viewer.SendStatusAsync("paired");
            await session.Host.SendStatusAsync("peer-joined");
            closeStatus = await ReceiveLoopAsync(socket, () => session.Host, context.RequestAborted);
        }
        finally
        {
            if (session.DetachViewer(viewer))
                await session.Host.SendStatusAsync("peer-left");
            await viewer.CloseAsync(closeStatus);
            await viewer.CompleteAsync();
            _logger.LogInformation("Viewer disconnected: id {Id}, ip {Ip}", id, ip);
        }
    }

    /// <summary>Accepted-then-rejected socket: send close frame and wait briefly for the client's reply.</summary>
    private static async Task RejectAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseAsync(status, null, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Reads whole messages (reassembling fragments, up to MaxMessageBytes) and forwards binary
    /// ones to the current peer. Text messages from clients are ignored. Returns when the socket closes.
    /// </summary>
    private async Task<WebSocketCloseStatus> ReceiveLoopAsync(WebSocket socket, Func<PeerConnection?> target, CancellationToken cancellationToken)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                byte[] buffer = new byte[16 * 1024];
                int length = 0;
                WebSocketMessageType type;
                bool tooBig = false;
                while (true)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(chunk, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return WebSocketCloseStatus.NormalClosure;
                    type = result.MessageType;
                    if (length + result.Count > _options.MaxMessageBytes)
                    {
                        tooBig = true;
                        break;
                    }
                    if (length + result.Count > buffer.Length)
                        Array.Resize(ref buffer, Math.Min(_options.MaxMessageBytes, Math.Max(buffer.Length * 2, length + result.Count)));
                    Buffer.BlockCopy(chunk, 0, buffer, length, result.Count);
                    length += result.Count;
                    if (result.EndOfMessage)
                        break;
                }
                if (tooBig)
                {
                    return WebSocketCloseStatus.MessageTooBig;
                }
                if (type != WebSocketMessageType.Binary)
                    continue;
                PeerConnection? peer = target();
                if (peer is not null)
                    await peer.SendBinaryAsync(buffer, length, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            return WebSocketCloseStatus.NormalClosure;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
