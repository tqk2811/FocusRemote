using FocusRemote.Host.Core.Sessions;
using FocusRemote.Host.Core.Terminal;
using FocusRemote.Host.Core.Terminal.Interfaces;
using FocusRemote.Host.Core.Terminal.Models;
using FocusRemote.Host.Tests.Helpers;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models.Terminal;

namespace FocusRemote.Host.Tests;

/// <summary><see cref="TerminalManager"/> alone, on an <see cref="OutboundQueue"/> nobody drains (a stalled link).</summary>
public sealed class TerminalManagerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OutputBlockedByBackpressure_ReturnsWhenTerminalClosed()
    {
        var factory = new FakeFactory();
        using var manager = CreateManager(factory, new OutboundQueue());
        manager.Handle(TerminalType.Open, ControlMessages.SerializeJson(new TerminalOpenMessage { TerminalId = 1, ShellId = "cmd", Cols = 80, Rows = 24 }));
        FakeProcess process = Assert.Single(factory.Started);

        // Fill the queue past the limit; nothing sends it.
        process.Listener.OnOutput(new byte[TerminalManager.MaxPendingOutputBytes + 1]);
        Task blocked = Task.Run(() => process.Listener.OnOutput(new byte[16]));
        await Task.Delay(100);
        Assert.False(blocked.IsCompleted);

        manager.Handle(TerminalType.Close, TerminalMessages.BuildClose(1));
        await blocked.WaitAsync(_timeout);
    }

    [Fact]
    public void OpenWithoutShellId_ClosedShellNotFound()
    {
        var outbound = new OutboundQueue();
        using var manager = CreateManager(new FakeFactory(), outbound);
        manager.Handle(TerminalType.Open, "{\"terminalId\":4,\"shellId\":null,\"cols\":80,\"rows\":24}"u8.ToArray());

        Assert.True(outbound.TryDequeue(out var message));
        Assert.Equal((byte)TerminalType.Closed, message.Type);
        Assert.True(ControlMessages.TryDeserializeJson(message.Payload, out TerminalClosedMessage? closed));
        Assert.Equal((4u, "shell_not_found"), (closed!.TerminalId, closed.Reason));
    }

    [Fact]
    public async Task Dispose_DisposesProcessesInParallel()
    {
        var factory = new FakeFactory { DisposeDelay = TimeSpan.FromMilliseconds(500) };
        var manager = CreateManager(factory, new OutboundQueue());
        for (uint id = 1; id <= 4; id++)
            manager.Handle(TerminalType.Open, ControlMessages.SerializeJson(new TerminalOpenMessage { TerminalId = id, ShellId = "cmd", Cols = 80, Rows = 24 }));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(manager.Dispose).WaitAsync(_timeout);
        Assert.All(factory.Started, p => Assert.True(p.Disposed));
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1500), $"took {watch.Elapsed}");
    }

    private static TerminalManager CreateManager(FakeFactory factory, OutboundQueue outbound)
        => new(true, new OneShellCatalog(), factory, outbound, new MemoryLog(), "test", _ => { });

    private sealed class FakeFactory : ITerminalProcessFactory
    {
        public List<FakeProcess> Started { get; } = [];
        public TimeSpan DisposeDelay { get; init; }

        public ITerminalProcess Start(ShellDefinition shell, int cols, int rows, ITerminalProcessListener listener)
        {
            var process = new FakeProcess(listener, DisposeDelay);
            lock (Started)
                Started.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess(ITerminalProcessListener listener, TimeSpan disposeDelay) : ITerminalProcess
    {
        public ITerminalProcessListener Listener => listener;
        public volatile bool Disposed;

        public bool Write(ReadOnlyMemory<byte> data) => true;

        public void Resize(int cols, int rows)
        {
        }

        public void Dispose()
        {
            Thread.Sleep(disposeDelay);
            Disposed = true;
        }
    }

    private sealed class OneShellCatalog : IShellCatalog
    {
        private readonly ShellDefinition _shell = new() { Id = "cmd", Name = "Command Prompt", CommandLine = "cmd.exe" };

        public IReadOnlyList<ShellDefinition> GetShells() => [_shell];

        public ShellDefinition? Find(string id) => id == _shell.Id ? _shell : null;
    }
}
