using System.Security.Cryptography;
using System.Threading.Channels;
using FocusRemote.Host.Core.Audio.Interfaces;
using FocusRemote.Host.Core.Input.Interfaces;
using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Core.Sessions;
using FocusRemote.Host.Core.Sessions.Interfaces;
using FocusRemote.Host.Core.Sources.Enums;
using FocusRemote.Host.Core.Sources.Interfaces;
using FocusRemote.Host.Core.Sources.Models;
using FocusRemote.Host.Core.Video.Interfaces;
using FocusRemote.Host.Tests.Helpers;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Handshake;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models;
using FocusRemote.Protocol.Messages.Models.Control;
using FocusRemote.Protocol.Messages.Models.Input;
using Channel = FocusRemote.Protocol.Enums.Channel;

namespace FocusRemote.Host.Tests;

/// <summary>Audio is started only when SelectSource asks for it AND the viewer listed "opus" (protocol §5.1/§5.4).</summary>
public sealed class AudioSessionTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly CaptureSource _source = new()
    {
        Id = "m:0", Kind = SourceKind.Monitor, Title = "Test", Handle = IntPtr.Zero, Bounds = new PixelRect(0, 0, 640, 480),
    };

    [Fact]
    public async Task OpusViewerAskingForAudio_ReceivesConfigAndPackets()
    {
        await using var harness = await Harness.StartAsync();
        await harness.SendControlAsync(ControlType.Capabilities, new CapabilitiesMessage { VideoCodecs = ["h264"], AudioCodecs = ["opus"] });
        await harness.SendControlAsync(ControlType.SelectSource, Select(audio: true));

        byte[] config = await harness.ReceiveUntilAsync(Channel.Audio, (byte)AudioType.Config);
        Assert.True(AudioMessages.TryParseConfig(config, out AudioConfigMessage? message));
        Assert.Equal("opus", message!.Codec);
        byte[] packet = await harness.ReceiveUntilAsync(Channel.Audio, (byte)AudioType.Packet);
        Assert.True(AudioMessages.TryParsePacket(packet, out AudioPacketMessage? parsed));
        Assert.Equal([1, 2, 3], parsed!.Data.ToArray());
        Assert.Equal(1, harness.AudioStreamersCreated);
    }

    [Theory]
    [InlineData(false, true)]  // viewer cannot decode Opus
    [InlineData(true, false)]  // viewer did not ask for audio
    public async Task NoAudioStreamer_WhenViewerLacksOpusOrDoesNotAsk(bool viewerHasOpus, bool askAudio)
    {
        await using var harness = await Harness.StartAsync();
        await harness.SendControlAsync(ControlType.Capabilities, new CapabilitiesMessage { VideoCodecs = ["h264"], AudioCodecs = viewerHasOpus ? ["opus"] : [] });
        await harness.SendControlAsync(ControlType.SelectSource, Select(askAudio));
        await harness.ReceiveUntilAsync(Channel.Video, (byte)VideoType.Frame);
        Assert.Equal(0, harness.AudioStreamersCreated);
    }

    [Fact]
    public async Task SetAudio_TogglesAudioOfRunningStream()
    {
        await using var harness = await Harness.StartAsync();
        await harness.SendControlAsync(ControlType.Capabilities, new CapabilitiesMessage { VideoCodecs = ["h264"], AudioCodecs = ["opus"] });
        await harness.SendControlAsync(ControlType.SelectSource, Select(audio: false)); // viewer starts muted
        await harness.ReceiveUntilAsync(Channel.Video, (byte)VideoType.Frame);
        Assert.Equal(0, harness.AudioStreamersCreated);

        await harness.SendControlAsync(ControlType.SetAudio, new SetAudioMessage { Enabled = true });
        await harness.ReceiveUntilAsync(Channel.Audio, (byte)AudioType.Config);
        Assert.Equal(1, harness.AudioStreamersCreated);
        Assert.Equal(0, harness.AudioStreamersDisposed);

        await harness.SendControlAsync(ControlType.SetAudio, new SetAudioMessage { Enabled = true }); // already on: no-op
        await harness.SendControlAsync(ControlType.SetAudio, new SetAudioMessage { Enabled = false });
        await harness.SendControlAsync(ControlType.SetAudio, new SetAudioMessage { Enabled = true });
        await harness.ReceiveUntilAsync(Channel.Audio, (byte)AudioType.Config);
        await harness.SendControlAsync(ControlType.ListSources, new { });
        await harness.ReceiveUntilAsync(Channel.Control, (byte)ControlType.SourceList);
        Assert.Equal(2, harness.AudioStreamersCreated);
        Assert.Equal(1, harness.AudioStreamersDisposed);
    }

    [Fact]
    public async Task SetAudio_IgnoredWithoutStreamOrOpus()
    {
        await using var harness = await Harness.StartAsync();
        await harness.SendControlAsync(ControlType.Capabilities, new CapabilitiesMessage { VideoCodecs = ["h264"], AudioCodecs = [] });
        await harness.SendControlAsync(ControlType.SetAudio, new SetAudioMessage { Enabled = true }); // no stream yet
        await harness.SendControlAsync(ControlType.SelectSource, Select(audio: false));
        await harness.SendControlAsync(ControlType.SetAudio, new SetAudioMessage { Enabled = true }); // no opus
        await harness.SendControlAsync(ControlType.ListSources, new { }); // reply proves the SetAudio before it was handled
        await harness.ReceiveUntilAsync(Channel.Control, (byte)ControlType.SourceList);
        Assert.Equal(0, harness.AudioStreamersCreated);
    }

    [Fact]
    public async Task SecondViewer_WhileBusy_GetsBusyFrame()
    {
        await using var harness = await Harness.StartAsync();
        var second = new MemoryLink();
        Task run = harness.Manager.RunAsync(second, CancellationToken.None);
        using var cts = new CancellationTokenSource(_timeout);
        byte[] frame = await second.ToViewer.Reader.ReadAsync(cts.Token);
        Assert.True(HandshakeFrames.IsBusy(frame));
        await run.WaitAsync(_timeout);
    }

    private static SelectSourceMessage Select(bool audio) => new()
    {
        Id = _source.Id, MaxWidth = 640, MaxHeight = 480, MaxFps = 30, BitrateKbps = 1000, Audio = audio,
    };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly MemoryLink _link = new();
        private readonly CancellationTokenSource _cts = new();
        private Task _run = Task.CompletedTask;
        private SecureSession? _session;
        private int _audioStreamersCreated;

        public HostSessionManager Manager { get; private set; } = null!;

        private int _audioStreamersDisposed;

        public int AudioStreamersCreated => Volatile.Read(ref _audioStreamersCreated);

        public int AudioStreamersDisposed => Volatile.Read(ref _audioStreamersDisposed);

        public static async Task<Harness> StartAsync()
        {
            var harness = new Harness();
            byte[] rootKey = RandomNumberGenerator.GetBytes(32);
            var manager = new HostSessionManager(
                () => rootKey,
                new FakeCatalog(),
                new MemoryLog(),
                streamerFactory: (source, _, sink) => new FakeVideoStreamer(source, sink),
                inputInjectorFactory: () => new NullInjector(),
                audioStreamerFactory: (_, sink) =>
                {
                    Interlocked.Increment(ref harness._audioStreamersCreated);
                    return new FakeAudioStreamer(sink, () => Interlocked.Increment(ref harness._audioStreamersDisposed));
                });
            harness.Manager = manager;
            harness._run = manager.RunAsync(harness._link, harness._cts.Token);

            var handshake = new ViewerHandshake(rootKey);
            harness._link.ToHost.Writer.TryWrite(handshake.CreateHelloViewer());
            Assert.True(handshake.TryHandleHelloHost(await harness.ReadAsync(), out byte[] probe));
            harness._link.ToHost.Writer.TryWrite(probe);
            harness._session = handshake.Session!;
            await harness.ReceiveUntilAsync(Channel.Control, (byte)ControlType.AuthOk);
            return harness;
        }

        public Task SendControlAsync<T>(ControlType type, T message)
        {
            _link.ToHost.Writer.TryWrite(_session!.Seal(Channel.Control, (byte)type, ControlMessages.SerializeJson(message)));
            return Task.CompletedTask;
        }

        public async Task<byte[]> ReceiveUntilAsync(Channel channel, byte type)
        {
            while (true)
            {
                byte[] frame = await ReadAsync();
                Assert.True(_session!.TryOpen(frame, out byte c, out byte t, out byte[] payload));
                if (c == (byte)channel && t == type)
                    return payload;
            }
        }

        private async Task<byte[]> ReadAsync()
        {
            using var cts = new CancellationTokenSource(_timeout);
            return await _link.ToViewer.Reader.ReadAsync(cts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _link.ToHost.Writer.TryComplete();
            await _run.WaitAsync(_timeout);
            _cts.Dispose();
            _session?.Dispose();
        }
    }

    private sealed class MemoryLink : IViewerLink
    {
        public Channel<byte[]> ToHost { get; } = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public Channel<byte[]> ToViewer { get; } = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public string Origin => "memory";
        public ChannelReader<byte[]> Incoming => ToHost.Reader;

        public Task SendAsync(byte[] message, CancellationToken cancellationToken)
        {
            ToViewer.Writer.TryWrite(message);
            return Task.CompletedTask;
        }

        public void Close() => ToHost.Writer.TryComplete();
    }

    private sealed class FakeCatalog : ISourceCatalog
    {
        public IReadOnlyList<CaptureSource> GetSources() => [_source];
        public CaptureSource? Find(string id) => id == _source.Id ? _source : null;
        public PixelRect? GetCurrentBounds(CaptureSource source) => source.Bounds;
    }

    private sealed class FakeVideoStreamer(CaptureSource source, IVideoSink sink) : IVideoStreamer
    {
        public CaptureSource Source => source;

        public void Start() => sink.OnFrame(VideoMessages.BuildFrame(true, 0, [0, 0, 1, 0x65]));

        public void RequestKeyframe()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeAudioStreamer(IAudioSink sink, Action onDispose) : IAudioStreamer
    {
        public void Start()
        {
            sink.OnAudioConfig(new AudioConfigMessage { Codec = "opus", SampleRate = 48000, Channels = 2 });
            sink.OnAudioPacket(AudioMessages.BuildPacket(0, [1, 2, 3]));
        }

        public void Dispose() => onDispose();
    }

    private sealed class NullInjector : IInputInjector
    {
        public void Handle(InputMessage message, PixelRect target, IntPtr foregroundWindow)
        {
        }

        public void ReleaseAll()
        {
        }
    }
}
