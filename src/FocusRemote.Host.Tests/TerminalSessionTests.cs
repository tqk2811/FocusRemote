using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using FocusRemote.Host.Core.Input.Interfaces;
using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Core.Sessions;
using FocusRemote.Host.Core.Sessions.Interfaces;
using FocusRemote.Host.Core.Sources.Interfaces;
using FocusRemote.Host.Core.Sources.Models;
using FocusRemote.Host.Core.Terminal.Interfaces;
using FocusRemote.Host.Core.Terminal.Models;
using FocusRemote.Host.Tests.Helpers;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Handshake;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models.Input;
using FocusRemote.Protocol.Messages.Models.Terminal;
using Channel = FocusRemote.Protocol.Enums.Channel;

namespace FocusRemote.Host.Tests;

/// <summary>Terminal channel through a real <see cref="HostSessionManager"/> session with fake shells (protocol §5.5).</summary>
public sealed class TerminalSessionTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Disabled_ShellListDisabled_AndOpenClosedDisabled()
    {
        await using var harness = await Harness.StartAsync(enabled: false);
        Assert.False(harness.InitialShellList.Enabled);
        Assert.Empty(harness.InitialShellList.Shells);

        harness.SendOpen(1, "cmd");
        TerminalClosedMessage closed = await harness.ReceiveClosedAsync();
        Assert.Equal("disabled", closed.Reason);
        Assert.Empty(harness.Factory.Started);
    }

    [Fact]
    public async Task Enabled_OpenInputOutputResizeClose()
    {
        await using var harness = await Harness.StartAsync(enabled: true);
        Assert.True(harness.InitialShellList.Enabled);
        Assert.Equal(["cmd", "pwsh"], harness.InitialShellList.Shells.Select(s => s.Id));

        harness.SendOpen(7, "pwsh");
        TerminalOpenedMessage opened = await harness.ReceiveJsonAsync<TerminalOpenedMessage>(TerminalType.Opened);
        Assert.Equal(7u, opened.TerminalId);
        FakeProcess process = Assert.Single(harness.Factory.Started);
        Assert.Equal("pwsh", process.Shell.Id);
        Assert.Equal((100, 30), (process.Cols, process.Rows));

        harness.SendTerminal(TerminalType.Input, TerminalMessages.BuildData(7, "dir"u8));
        harness.SendTerminal(TerminalType.Resize, TerminalMessages.BuildResize(7, 90, 20));
        harness.SendTerminal(TerminalType.Input, TerminalMessages.BuildData(99, "x"u8)); // unknown id: ignored
        harness.SendTerminal(TerminalType.ListShells, ControlMessages.SerializeEmpty()); // barrier: the above were handled
        await harness.ReceiveJsonAsync<ShellListMessage>(TerminalType.ShellList);
        process.Listener.OnOutput("hello"u8);
        byte[] output = await harness.ReceiveUntilAsync(Channel.Terminal, (byte)TerminalType.Output);
        Assert.True(TerminalMessages.TryParseData(output, out TerminalDataMessage? data));
        Assert.Equal(7u, data!.TerminalId);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(data.Data.Span));
        Assert.Equal("dir", System.Text.Encoding.UTF8.GetString(process.Input.ToArray()));
        Assert.Equal((90, 20), (process.Cols, process.Rows));

        harness.SendTerminal(TerminalType.Close, TerminalMessages.BuildClose(7));
        TerminalClosedMessage closed = await harness.ReceiveClosedAsync();
        Assert.Equal("closed", closed.Reason);
        await process.DisposedTask.WaitAsync(_timeout); // disposed off the receive loop
    }

    [Fact]
    public async Task ShellExit_ReportsExitedWithCode()
    {
        await using var harness = await Harness.StartAsync(enabled: true);
        harness.SendOpen(1, "cmd");
        await harness.ReceiveJsonAsync<TerminalOpenedMessage>(TerminalType.Opened);
        harness.Factory.Started.Single().Listener.OnExited(3);
        TerminalClosedMessage closed = await harness.ReceiveClosedAsync();
        Assert.Equal(("exited", 3), (closed.Reason, closed.ExitCode!.Value));
    }

    [Fact]
    public async Task UnknownShell_DuplicateId_AndLimit()
    {
        await using var harness = await Harness.StartAsync(enabled: true);
        harness.SendOpen(100, "bash");
        Assert.Equal("shell_not_found", (await harness.ReceiveClosedAsync()).Reason);

        for (uint id = 1; id <= 8; id++)
        {
            harness.SendOpen(id, "cmd");
            await harness.ReceiveJsonAsync<TerminalOpenedMessage>(TerminalType.Opened);
        }
        harness.SendOpen(3, "cmd");
        Assert.Equal("duplicate_id", (await harness.ReceiveClosedAsync()).Reason);
        harness.SendOpen(9, "cmd");
        Assert.Equal("limit", (await harness.ReceiveClosedAsync()).Reason);
        Assert.Equal(8, harness.Factory.Started.Count);
        Assert.Equal(8, harness.LastTerminalCount);
    }

    [Fact]
    public async Task StartFailure_ClosedStartFailed()
    {
        await using var harness = await Harness.StartAsync(enabled: true);
        harness.Factory.FailNext = true;
        harness.SendOpen(1, "cmd");
        TerminalClosedMessage closed = await harness.ReceiveClosedAsync();
        Assert.Equal("start_failed", closed.Reason);
        Assert.Equal("boom", closed.Message);
    }

    [Fact]
    public async Task Disconnect_DisposesAllTerminals()
    {
        var harness = await Harness.StartAsync(enabled: true);
        harness.SendOpen(1, "cmd");
        harness.SendOpen(2, "pwsh");
        await harness.ReceiveJsonAsync<TerminalOpenedMessage>(TerminalType.Opened);
        await harness.ReceiveJsonAsync<TerminalOpenedMessage>(TerminalType.Opened);
        await harness.DisposeAsync();
        Assert.Equal(2, harness.Factory.Started.Count);
        Assert.All(harness.Factory.Started, p => Assert.True(p.Disposed));
        Assert.Equal(0, harness.LastTerminalCount);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly MemoryLink _link = new();
        private readonly CancellationTokenSource _cts = new();
        private Task _run = Task.CompletedTask;
        private SecureSession? _session;
        private int _lastTerminalCount = -1;
        private bool _disposed;

        public FakeFactory Factory { get; } = new();
        public ShellListMessage InitialShellList { get; private set; } = null!;
        public int LastTerminalCount => Volatile.Read(ref _lastTerminalCount);

        public static async Task<Harness> StartAsync(bool enabled)
        {
            var harness = new Harness();
            byte[] rootKey = RandomNumberGenerator.GetBytes(32);
            var manager = new HostSessionManager(
                () => rootKey,
                new FakeCatalog(),
                new MemoryLog(),
                inputInjectorFactory: () => new NullInjector(),
                terminalEnabled: enabled,
                shellCatalog: new FakeShellCatalog(),
                terminalProcessFactory: harness.Factory);
            manager.TerminalCountChanged += count => Volatile.Write(ref harness._lastTerminalCount, count);
            harness._run = manager.RunAsync(harness._link, harness._cts.Token);

            var handshake = new ViewerHandshake(rootKey);
            harness._link.ToHost.Writer.TryWrite(handshake.CreateHelloViewer());
            Assert.True(handshake.TryHandleHelloHost(await harness.ReadAsync(), out byte[] probe));
            harness._link.ToHost.Writer.TryWrite(probe);
            harness._session = handshake.Session!;
            harness.InitialShellList = await harness.ReceiveJsonAsync<ShellListMessage>(TerminalType.ShellList);
            return harness;
        }

        public void SendOpen(uint id, string shellId)
            => SendTerminal(TerminalType.Open, ControlMessages.SerializeJson(new TerminalOpenMessage { TerminalId = id, ShellId = shellId, Cols = 100, Rows = 30 }));

        public void SendTerminal(TerminalType type, byte[] payload)
            => _link.ToHost.Writer.TryWrite(_session!.Seal(Channel.Terminal, (byte)type, payload));

        public Task<TerminalClosedMessage> ReceiveClosedAsync() => ReceiveJsonAsync<TerminalClosedMessage>(TerminalType.Closed);

        public async Task<T> ReceiveJsonAsync<T>(TerminalType type) where T : class
        {
            byte[] payload = await ReceiveUntilAsync(Channel.Terminal, (byte)type);
            Assert.True(ControlMessages.TryDeserializeJson(payload, out T? message));
            return message!;
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
            if (_disposed)
                return;
            _disposed = true;
            _link.ToHost.Writer.TryComplete();
            await _run.WaitAsync(_timeout);
            _cts.Dispose();
            _session?.Dispose();
        }
    }

    private sealed class FakeFactory : ITerminalProcessFactory
    {
        public ConcurrentQueue<FakeProcess> StartedQueue { get; } = new();
        public IReadOnlyList<FakeProcess> Started => [.. StartedQueue];
        public volatile bool FailNext;

        public ITerminalProcess Start(ShellDefinition shell, int cols, int rows, ITerminalProcessListener listener)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("boom");
            }
            var process = new FakeProcess(shell, cols, rows, listener);
            StartedQueue.Enqueue(process);
            return process;
        }
    }

    private sealed class FakeProcess(ShellDefinition shell, int cols, int rows, ITerminalProcessListener listener) : ITerminalProcess
    {
        public ShellDefinition Shell => shell;
        public ITerminalProcessListener Listener => listener;
        public MemoryStream Input { get; } = new();
        public int Cols { get; private set; } = cols;
        public int Rows { get; private set; } = rows;
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed => _disposed.Task.IsCompleted;
        public Task DisposedTask => _disposed.Task;

        public bool Write(ReadOnlyMemory<byte> data)
        {
            lock (Input)
                Input.Write(data.Span);
            return true;
        }

        public void Resize(int newCols, int newRows) => (Cols, Rows) = (newCols, newRows);

        public void Dispose() => _disposed.TrySetResult();
    }

    private sealed class FakeShellCatalog : IShellCatalog
    {
        private readonly IReadOnlyList<ShellDefinition> _shells =
        [
            new ShellDefinition { Id = "cmd", Name = "Command Prompt", CommandLine = "cmd.exe" },
            new ShellDefinition { Id = "pwsh", Name = "PowerShell 7", CommandLine = "pwsh.exe" },
        ];

        public IReadOnlyList<ShellDefinition> GetShells() => _shells;

        public ShellDefinition? Find(string id) => _shells.FirstOrDefault(s => s.Id == id);
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
        public IReadOnlyList<CaptureSource> GetSources() => [];
        public CaptureSource? Find(string id) => null;
        public PixelRect? GetCurrentBounds(CaptureSource source) => null;
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
