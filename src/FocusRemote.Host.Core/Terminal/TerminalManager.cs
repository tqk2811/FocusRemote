using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Sessions;
using FocusRemote.Host.Core.Terminal.Interfaces;
using FocusRemote.Host.Core.Terminal.Models;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models.Terminal;

namespace FocusRemote.Host.Core.Terminal;

/// <summary>
/// Terminal channel of one viewer session (protocol §5.5): up to <see cref="MaxTerminals"/> shells,
/// all killed on <see cref="Dispose"/>. Output waits while too much of it is queued (backpressure).
/// </summary>
internal sealed class TerminalManager : IDisposable
{
    public const int MaxTerminals = 8;
    public const long MaxPendingOutputBytes = 1024 * 1024;
    private const int DefaultCols = 80;
    private const int DefaultRows = 24;

    private readonly bool _enabled;
    private readonly IShellCatalog _catalog;
    private readonly ITerminalProcessFactory _factory;
    private readonly OutboundQueue _outbound;
    private readonly IAppLog _log;
    private readonly string _origin;
    private readonly Action<int> _countChanged;
    private readonly object _lock = new();
    private readonly Dictionary<uint, OpenTerminal> _terminals = [];
    private volatile bool _disposed;

    public TerminalManager(bool enabled, IShellCatalog catalog, ITerminalProcessFactory factory, OutboundQueue outbound, IAppLog log, string origin, Action<int> countChanged)
    {
        _enabled = enabled;
        _catalog = catalog;
        _factory = factory;
        _outbound = outbound;
        _log = log;
        _origin = origin;
        _countChanged = countChanged;
    }

    public void SendShellList()
    {
        IReadOnlyList<ShellInfo> shells = _enabled
            ? _catalog.GetShells().Select(s => new ShellInfo { Id = s.Id, Name = s.Name }).ToList()
            : [];
        SendJson(TerminalType.ShellList, new ShellListMessage { Enabled = _enabled, Shells = shells });
    }

    public void Handle(TerminalType type, byte[] payload)
    {
        switch (type)
        {
            case TerminalType.ListShells:
                SendShellList();
                break;
            case TerminalType.Open:
                if (ControlMessages.TryDeserializeJson(payload, out TerminalOpenMessage? open))
                    Open(open!);
                break;
            case TerminalType.Input:
                if (TerminalMessages.TryParseData(payload, out TerminalDataMessage? input))
                    Write(input!);
                break;
            case TerminalType.Resize:
                if (TerminalMessages.TryParseResize(payload, out TerminalResizeMessage? resize))
                    Resize(resize!);
                break;
            case TerminalType.Close:
                if (TerminalMessages.TryParseClose(payload, out uint closeId))
                    Close(closeId);
                break;
            default:
                // Host→viewer types and unknown types are ignored.
                break;
        }
    }

    private void Open(TerminalOpenMessage open)
    {
        uint id = open.TerminalId;
        if (!_enabled)
        {
            SendClosed(id, "disabled", null, "Terminal từ xa đang tắt trên máy này.");
            return;
        }
        ShellDefinition? shell = open.ShellId is null ? null : _catalog.Find(open.ShellId);
        int count;
        lock (_lock)
        {
            if (_disposed)
                return;
            if (_terminals.ContainsKey(id))
            {
                SendClosed(id, "duplicate_id", null, null);
                return;
            }
            if (_terminals.Count >= MaxTerminals)
            {
                SendClosed(id, "limit", null, $"Tối đa {MaxTerminals} terminal mỗi phiên.");
                return;
            }
            if (shell is null)
            {
                SendClosed(id, "shell_not_found", null, "Không có shell: " + open.ShellId);
                return;
            }
            // Held while starting so that early output waits until Opened is queued.
            var terminal = new OpenTerminal(this, id, shell.Id);
            try
            {
                terminal.Process = _factory.Start(shell, open.Cols > 0 ? open.Cols : DefaultCols, open.Rows > 0 ? open.Rows : DefaultRows, terminal);
            }
            catch (Exception ex)
            {
                _log.Warn($"Viewer {_origin}: terminal {id} ({shell.Id}) failed to start", ex);
                SendClosed(id, "start_failed", null, ex.Message);
                return;
            }
            _terminals.Add(id, terminal);
            count = _terminals.Count;
            SendJson(TerminalType.Opened, new TerminalOpenedMessage { TerminalId = id, ShellId = shell.Id });
        }
        _log.Info($"Viewer {_origin}: terminal {id} opened ({shell.Id})");
        _countChanged(count);
    }

    private void Close(uint id)
    {
        OpenTerminal? terminal;
        int count;
        lock (_lock)
        {
            if (!_terminals.Remove(id, out terminal))
                return;
            count = _terminals.Count;
        }
        terminal.MarkRemoved();
        SendClosed(id, "closed", null, null);
        _log.Info($"Viewer {_origin}: terminal {id} closed");
        _countChanged(count);
        // Killing and joining the shell can take seconds: keep it off the receive loop.
        _ = Task.Run(() => DisposeProcess(terminal));
    }

    private void Write(TerminalDataMessage input)
    {
        OpenTerminal? terminal = Find(input.TerminalId);
        if (terminal?.Process is null || terminal.Process.Write(input.Data) || terminal.InputDropLogged)
            return;
        terminal.InputDropLogged = true;
        _log.Warn($"Viewer {_origin}: terminal {terminal.Id} input dropped (shell not reading, queue full)");
    }

    private void Resize(TerminalResizeMessage resize)
    {
        try
        {
            Find(resize.TerminalId)?.Process?.Resize(resize.Cols, resize.Rows);
        }
        catch (Exception ex)
        {
            _log.Warn($"Viewer {_origin}: terminal {resize.TerminalId} resize to {resize.Cols}x{resize.Rows} failed", ex);
        }
    }

    private void DisposeProcess(OpenTerminal terminal)
    {
        try
        {
            terminal.Process?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"Viewer {_origin}: terminal {terminal.Id} dispose failed", ex);
        }
    }

    private OpenTerminal? Find(uint id)
    {
        lock (_lock)
            return _terminals.GetValueOrDefault(id);
    }

    private bool IsCurrent(OpenTerminal terminal)
        => _terminals.TryGetValue(terminal.Id, out OpenTerminal? current) && ReferenceEquals(current, terminal);

    /// <summary>Process reader thread.</summary>
    internal void OnOutput(OpenTerminal terminal, ReadOnlySpan<byte> data)
    {
        // Returns early once the terminal is removed, so its reader keeps draining the pipe (ClosePseudoConsole needs that).
        _outbound.WaitForTerminalOutputBelow(MaxPendingOutputBytes, terminal.Removed);
        lock (_lock)
        {
            if (_disposed || !IsCurrent(terminal))
                return;
            _outbound.EnqueueTerminalOutput(TerminalMessages.BuildData(terminal.Id, data));
        }
    }

    /// <summary>Process waiter thread, after all output was delivered.</summary>
    internal void OnExited(OpenTerminal terminal, int exitCode)
    {
        int count;
        lock (_lock)
        {
            if (_disposed || !IsCurrent(terminal))
                return;
            _terminals.Remove(terminal.Id);
            count = _terminals.Count;
        }
        terminal.MarkRemoved();
        SendClosed(terminal.Id, "exited", exitCode, null);
        _log.Info($"Viewer {_origin}: terminal {terminal.Id} exited ({exitCode})");
        _countChanged(count);
    }

    private void SendClosed(uint id, string reason, int? exitCode, string? message)
        => SendJson(TerminalType.Closed, new TerminalClosedMessage { TerminalId = id, Reason = reason, ExitCode = exitCode, Message = message });

    private void SendJson<T>(TerminalType type, T message)
        => _outbound.Enqueue(Channel.Terminal, (byte)type, ControlMessages.SerializeJson(message));

    /// <summary>Kills every terminal (viewer disconnected); no Closed messages are sent.</summary>
    public void Dispose()
    {
        List<OpenTerminal> terminals;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            terminals = [.. _terminals.Values];
            _terminals.Clear();
        }
        foreach (OpenTerminal terminal in terminals)
            terminal.MarkRemoved();
        // In parallel: each one may wait seconds for its threads; the session waits for the slowest only.
        Task.WaitAll([.. terminals.Select(t => Task.Run(() => DisposeProcess(t)))]);
        if (terminals.Count > 0)
        {
            _log.Info($"Viewer {_origin}: {terminals.Count} terminal(s) killed");
            _countChanged(0);
        }
    }
}
