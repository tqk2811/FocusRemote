using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FocusRemote.Host.Core.Lan;
using FocusRemote.Host.Core.Sessions;
using FocusRemote.Host.Core.Sources;
using FocusRemote.Host.Tests.Helpers;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models.Control;

namespace FocusRemote.Host.Tests;

/// <summary>Real Kestrel HTTPS on a free port with a freshly generated self-signed certificate.</summary>
public sealed class LanServerTests : IAsyncLifetime
{
    private const string Id = "lan-test-id";
    private readonly byte[] _rootKey = RandomNumberGenerator.GetBytes(32);
    private readonly MemoryLog _log = new();
    private X509Certificate2? _certificate;
    private LanServer? _server;

    public async Task InitializeAsync()
    {
        byte[] pfx = LanCertificateManager.CreatePfx("testhost", [IPAddress.Parse("192.168.77.5")]);
        _certificate = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);
        var manager = new HostSessionManager(() => _rootKey, new SourceCatalog(), _log);
        _server = new LanServer(0, _certificate, FindWebRoot(), () => Id, manager, _log);
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        _certificate?.Dispose();
    }

    private static string? FindWebRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "FocusRemote.Relay", "wwwroot");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private async Task<ClientWebSocket> ConnectAsync(string id)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var cts = new CancellationTokenSource(TestViewer.Timeout);
        await socket.ConnectAsync(new Uri($"wss://localhost:{_server!.BoundPort}/ws/viewer/{id}"), cts.Token);
        return socket;
    }

    [Fact]
    public void Certificate_HasSanForMachineAndAddresses()
    {
        X509SubjectAlternativeNameExtension san = _certificate!.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("testhost", san.EnumerateDnsNames());
        Assert.Contains(IPAddress.Parse("192.168.77.5"), san.EnumerateIPAddresses());
        Assert.True(_certificate.HasPrivateKey);
    }

    [Fact]
    public async Task ServesWebViewerOverHttps()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler) { Timeout = TestViewer.Timeout };
        string html = await http.GetStringAsync($"https://localhost:{_server!.BoundPort}/");
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("other-id", 4004)]
    [InlineData("x", 4000)]
    public async Task WrongId_IsClosedWithRelayCode(string id, int code)
    {
        using TestViewer viewer = new(await ConnectAsync(id));
        (WebSocketMessageType type, _, WebSocketCloseStatus? status) = await viewer.ReceiveAsync();
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(code, (int?)status);
    }

    [Fact]
    public async Task Viewer_AuthenticatesAndGetsSourceList_SecondViewerRejected()
    {
        using TestViewer viewer = new(await ConnectAsync(Id));
        await viewer.ExpectStatusAsync("paired");
        Assert.True(await viewer.HandshakeAsync(_rootKey), _log.ToString());
        byte[] payload = await viewer.ReceiveUntilAsync(Channel.Control, (byte)ControlType.SourceList);
        Assert.True(ControlMessages.TryDeserializeJson(payload, out SourceListMessage? list));
        Assert.Contains(list!.Sources, s => s.Kind == "monitor");

        // Ping → Pong with the same 8 bytes.
        byte[] ping = ControlMessages.BuildPing(123456789);
        await viewer.SendAsync(viewer.Session.Seal(Channel.Control, (byte)ControlType.Ping, ping));
        Assert.Equal(ping, await viewer.ReceiveUntilAsync(Channel.Control, (byte)ControlType.Pong));

        using TestViewer second = new(await ConnectAsync(Id));
        (WebSocketMessageType type, _, WebSocketCloseStatus? status) = await second.ReceiveAsync();
        Assert.Equal(WebSocketMessageType.Close, type);
        Assert.Equal(4009, (int?)status);
    }
}
