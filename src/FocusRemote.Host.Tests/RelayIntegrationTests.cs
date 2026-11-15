using System.Net.WebSockets;
using System.Security.Cryptography;
using FocusRemote.Host.Core.Relay;
using FocusRemote.Host.Core.Relay.Enums;
using FocusRemote.Host.Core.Sessions;
using FocusRemote.Host.Core.Sources;
using FocusRemote.Host.Tests.Helpers;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models;
using FocusRemote.Protocol.Messages.Models.Control;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace FocusRemote.Host.Tests;

/// <summary>
/// End to end through the real Relay (in-process TestServer): host session + C# viewer.
/// The streaming test needs a desktop session and a D3D11 GPU (real capture + hardware encoder).
/// </summary>
public sealed class RelayIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RelayIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        => _factory.Server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);

    private async Task<(RelayHostClient Client, string Id)> StartHostAsync(byte[] rootKey, MemoryLog log)
    {
        string id = "h_" + Guid.NewGuid().ToString("N")[..16];
        var manager = new HostSessionManager(() => rootKey, new SourceCatalog(), log);
        var client = new RelayHostClient("http://localhost", id, manager, log, ConnectAsync);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (state, _) =>
        {
            if (state == RelayState.Registered)
                registered.TrySetResult();
        };
        client.Start();
        await registered.Task.WaitAsync(TestViewer.Timeout);
        return (client, id);
    }

    private async Task<TestViewer> ConnectViewerAsync(string id)
    {
        using var cts = new CancellationTokenSource(TestViewer.Timeout);
        WebSocket socket = await ConnectAsync(new Uri($"ws://localhost/ws/viewer/{id}"), cts.Token);
        var viewer = new TestViewer(socket);
        await viewer.ExpectStatusAsync("paired");
        return viewer;
    }

    [Fact]
    public async Task WrongPassword_GetsAuthFail()
    {
        var log = new MemoryLog();
        (RelayHostClient host, string id) = await StartHostAsync(RandomNumberGenerator.GetBytes(32), log);
        await using (host)
        {
            using TestViewer viewer = await ConnectViewerAsync(id);
            Assert.False(await viewer.HandshakeAsync(RandomNumberGenerator.GetBytes(32)), log.ToString());
        }
    }

    [Fact]
    public async Task Viewer_AuthenticatesListsSourcesAndReceivesKeyframe()
    {
        var log = new MemoryLog();
        byte[] rootKey = RandomNumberGenerator.GetBytes(32);
        (RelayHostClient host, string id) = await StartHostAsync(rootKey, log);
        await using (host)
        {
            using TestViewer viewer = await ConnectViewerAsync(id);
            Assert.True(await viewer.HandshakeAsync(rootKey), log.ToString());

            byte[] listPayload = await viewer.ReceiveUntilAsync(Channel.Control, (byte)ControlType.SourceList);
            Assert.True(ControlMessages.TryDeserializeJson(listPayload, out SourceListMessage? list));
            SourceInfo monitor = Assert.Single(list!.Sources, s => s.Id == "m:0");
            Assert.Equal("monitor", monitor.Kind);

            await viewer.SendControlAsync(ControlType.Capabilities, new CapabilitiesMessage { VideoCodecs = ["h264"], AudioCodecs = [] });
            await viewer.SendControlAsync(ControlType.SelectSource, new SelectSourceMessage
            {
                Id = "m:0",
                MaxWidth = 1280,
                MaxHeight = 720,
                MaxFps = 30,
                BitrateKbps = 3000,
                Audio = false,
            });

            byte[] startedPayload = await viewer.ReceiveUntilAsync(Channel.Control, (byte)ControlType.StreamStarted);
            Assert.True(ControlMessages.TryDeserializeJson(startedPayload, out StreamStartedMessage? started), log.ToString());
            Assert.Equal("m:0", started!.SourceId);
            Assert.Equal("h264", started.Codec);
            Assert.True(started.Width <= 1280 && started.Height <= 720 && started.Width % 2 == 0 && started.Height % 2 == 0);

            byte[] framePayload = await viewer.ReceiveUntilAsync(Channel.Video, (byte)VideoType.Frame);
            Assert.True(VideoMessages.TryParseFrame(framePayload, out VideoFrameMessage? frame));
            Assert.True(frame!.IsKeyframe, "first frame must be a keyframe");
            ReadOnlySpan<byte> data = frame.Data.Span;
            Assert.True(data.Length > 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1)), "Annex B start code expected");

            await viewer.SendControlAsync(ControlType.StopStream, EmptyStopMessage());
            byte[] stopped = await viewer.ReceiveUntilAsync(Channel.Control, (byte)ControlType.StreamStopped);
            Assert.True(ControlMessages.TryDeserializeJson(stopped, out StreamStoppedMessage? stoppedMessage));
            Assert.Equal("m:0", stoppedMessage!.SourceId);
        }
    }

    private static object EmptyStopMessage() => new { };
}
