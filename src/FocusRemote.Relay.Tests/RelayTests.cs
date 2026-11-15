using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace FocusRemote.Relay.Tests;

public sealed class RelayTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly WebApplicationFactory<Program> _factory;

    public RelayTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static string NewId() => "id_" + Guid.NewGuid().ToString("N");

    private async Task<WebSocket> ConnectAsync(string role, string id)
    {
        WebSocketClient client = _factory.Server.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(Timeout);
        return await client.ConnectAsync(new Uri($"ws://localhost/ws/{role}/{id}"), cts.Token);
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data, WebSocketCloseStatus? CloseStatus)> ReceiveAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var stream = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return (result.MessageType, Array.Empty<byte>(), result.CloseStatus);
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return (result.MessageType, stream.ToArray(), null);
        }
    }

    private static async Task ExpectStatusAsync(WebSocket socket, string type)
    {
        var message = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, message.Type);
        Assert.Equal("{\"type\":\"" + type + "\"}", Encoding.UTF8.GetString(message.Data));
    }

    private static async Task ExpectCloseAsync(WebSocket socket, int code)
    {
        var message = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Close, message.Type);
        Assert.Equal(code, (int?)message.CloseStatus);
    }

    private static Task SendBinaryAsync(WebSocket socket, byte[] data)
    {
        using var cts = new CancellationTokenSource(Timeout);
        return socket.SendAsync(data, WebSocketMessageType.Binary, true, cts.Token);
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
    }

    private async Task<(WebSocket Host, WebSocket Viewer)> PairAsync(string id)
    {
        WebSocket host = await ConnectAsync("host", id);
        await ExpectStatusAsync(host, "registered");
        WebSocket viewer = await ConnectAsync("viewer", id);
        await ExpectStatusAsync(viewer, "paired");
        await ExpectStatusAsync(host, "peer-joined");
        return (host, viewer);
    }

    [Fact]
    public async Task Pairing_ForwardsBinaryBothDirections_IncludingLargeMessage()
    {
        var (host, viewer) = await PairAsync(NewId());

        byte[] small = { 0x01, 0x01, 0xAA, 0xBB };
        await SendBinaryAsync(viewer, small);
        var received = await ReceiveAsync(host);
        Assert.Equal(WebSocketMessageType.Binary, received.Type);
        Assert.Equal(small, received.Data);

        byte[] large = new byte[2 * 1024 * 1024];
        new Random(42).NextBytes(large);
        await SendBinaryAsync(host, large);
        received = await ReceiveAsync(viewer);
        Assert.Equal(WebSocketMessageType.Binary, received.Type);
        Assert.Equal(large, received.Data);

        // Text from clients is ignored; the next binary still arrives in order.
        await viewer.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);
        await SendBinaryAsync(viewer, new byte[] { 7 });
        received = await ReceiveAsync(host);
        Assert.Equal(new byte[] { 7 }, received.Data);
    }

    [Fact]
    public async Task Viewer_WithoutHost_Closes4004()
    {
        WebSocket viewer = await ConnectAsync("viewer", NewId());
        await ExpectCloseAsync(viewer, 4004);
    }

    [Theory]
    [InlineData("host", "ab")]
    [InlineData("host", "bad.id")]
    [InlineData("viewer", "has%20space")]
    public async Task InvalidId_Closes4000(string role, string id)
    {
        WebSocket socket = await ConnectAsync(role, id);
        await ExpectCloseAsync(socket, 4000);
    }

    [Fact]
    public async Task DuplicateHost_Closes4009()
    {
        string id = NewId();
        WebSocket host = await ConnectAsync("host", id);
        await ExpectStatusAsync(host, "registered");
        WebSocket second = await ConnectAsync("host", id);
        await ExpectCloseAsync(second, 4009);
    }

    [Fact]
    public async Task SecondViewer_Closes4009()
    {
        string id = NewId();
        var (_, _) = await PairAsync(id);
        WebSocket second = await ConnectAsync("viewer", id);
        await ExpectCloseAsync(second, 4009);
    }

    [Fact]
    public async Task HostLeaving_NotifiesViewerAndClosesNormally()
    {
        var (host, viewer) = await PairAsync(NewId());
        await CloseAsync(host);
        await ExpectStatusAsync(viewer, "peer-left");
        await ExpectCloseAsync(viewer, (int)WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task ViewerLeaving_HostStaysAndAcceptsNewViewer()
    {
        string id = NewId();
        var (host, viewer) = await PairAsync(id);
        await CloseAsync(viewer);
        await ExpectCloseAsync(viewer, (int)WebSocketCloseStatus.NormalClosure);
        await ExpectStatusAsync(host, "peer-left");
        Assert.Equal(WebSocketState.Open, host.State);

        WebSocket viewer2 = await ConnectAsync("viewer", id);
        await ExpectStatusAsync(viewer2, "paired");
        await ExpectStatusAsync(host, "peer-joined");
        await SendBinaryAsync(viewer2, new byte[] { 1, 2, 3 });
        var received = await ReceiveAsync(host);
        Assert.Equal(new byte[] { 1, 2, 3 }, received.Data);
    }

    [Fact]
    public async Task StaticIndex_ServedWithNoCache()
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoCache);
    }
}
