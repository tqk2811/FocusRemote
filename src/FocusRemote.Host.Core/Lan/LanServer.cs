using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Sessions;
using FocusRemote.Protocol.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace FocusRemote.Host.Core.Lan;

/// <summary>
/// Embedded HTTPS server for LAN self-hosting: serves the WebViewer static files and accepts the viewer
/// WebSocket at <c>/ws/viewer/{id}</c> with the same close codes as the relay (4000/4004/4009, protocol §1).
/// The host side is in-process: the accepted socket is handed to <see cref="HostSessionManager"/> directly.
/// </summary>
public sealed class LanServer : IAsyncDisposable
{
    private const WebSocketCloseStatus InvalidId = (WebSocketCloseStatus)4000;
    private const WebSocketCloseStatus NoHost = (WebSocketCloseStatus)4004;
    private const WebSocketCloseStatus Conflict = (WebSocketCloseStatus)4009;

    private readonly int _port;
    private readonly X509Certificate2 _certificate;
    private readonly string? _webRoot;
    private readonly Func<string> _idProvider;
    private readonly HostSessionManager _manager;
    private readonly IAppLog _log;
    private readonly CancellationTokenSource _stop = new();
    private WebApplication? _app;

    /// <param name="port">TCP port on all interfaces; 0 picks a free port (see <see cref="BoundPort"/>).</param>
    /// <param name="webRoot">Folder with the WebViewer files; null/missing = WebSocket only.</param>
    public LanServer(int port, X509Certificate2 certificate, string? webRoot, Func<string> idProvider, HostSessionManager manager, IAppLog log)
    {
        _port = port;
        _certificate = certificate;
        _webRoot = webRoot;
        _idProvider = idProvider;
        _manager = manager;
        _log = log;
    }

    /// <summary>Actual port after <see cref="StartAsync"/>.</summary>
    public int BoundPort { get; private set; }

    public async Task StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, _port, listen => listen.UseHttps(_certificate));
            options.Limits.MaxRequestBodySize = 1024 * 1024;
        });

        WebApplication app = builder.Build();
        if (_webRoot is not null && Directory.Exists(_webRoot))
        {
            var files = new PhysicalFileProvider(_webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            var contentTypes = new FileExtensionContentTypeProvider();
            contentTypes.Mappings[".js"] = "text/javascript";
            contentTypes.Mappings[".mjs"] = "text/javascript";
            contentTypes.Mappings[".wasm"] = "application/wasm";
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                ContentTypeProvider = contentTypes,
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
            });
        }
        else
        {
            _log.Warn($"LAN: WebViewer folder not found ({_webRoot}); serving WebSocket only");
        }
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Map("/ws/viewer/{id}", (HttpContext context, string id) => HandleViewerAsync(context, id));

        await app.StartAsync();
        _app = app;
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel reported no address.");
        BoundPort = int.Parse(address[(address.LastIndexOf(':') + 1)..].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture);
        _log.Info($"LAN: listening on https://0.0.0.0:{BoundPort}");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_app is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _app.StopAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
            await _app.DisposeAsync();
            _app = null;
        }
        _stop.Dispose();
    }

    private async Task HandleViewerAsync(HttpContext context, string id)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        string origin = "LAN " + (context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?");
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        var link = new WebSocketViewerLink(socket, origin);

        WebSocketCloseStatus? reject = null;
        if (!CredentialValidator.IsValidId(id))
            reject = InvalidId;
        else if (!string.Equals(id, _idProvider(), StringComparison.Ordinal))
            reject = NoHost;
        else if (_manager.IsBusy)
            reject = Conflict;
        if (reject is not null)
        {
            _log.Info($"{origin}: viewer rejected with {(int)reject.Value}");
            await link.CloseWithStatusAsync(reject.Value);
            await DrainUntilClosedAsync(link);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
        _log.Info($"{origin}: viewer connected");
        await link.SendStatusAsync("paired", cts.Token);
        Task pump = link.PumpAsync(cts.Token);
        await _manager.RunAsync(link, cts.Token);
        link.Close();
        await pump;
    }

    private static async Task DrainUntilClosedAsync(WebSocketViewerLink link)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await link.PumpAsync(timeout.Token);
    }
}
