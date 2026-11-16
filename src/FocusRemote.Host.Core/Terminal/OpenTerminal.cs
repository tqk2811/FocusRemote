using FocusRemote.Host.Core.Terminal.Interfaces;

namespace FocusRemote.Host.Core.Terminal;

/// <summary>One terminal of a <see cref="TerminalManager"/>: forwards its process callbacks with its identity.</summary>
internal sealed class OpenTerminal : ITerminalProcessListener
{
    private readonly TerminalManager _owner;

    public OpenTerminal(TerminalManager owner, uint id, string shellId)
    {
        _owner = owner;
        Id = id;
        ShellId = shellId;
    }

    public uint Id { get; }

    public string ShellId { get; }

    /// <summary>Set once the process started (under the manager lock).</summary>
    public ITerminalProcess? Process { get; set; }

    /// <summary>Cancelled once the terminal leaves the manager (closed, exited, disposed): ends backpressure waits.</summary>
    public CancellationToken Removed => _removed.Token;

    /// <summary>Set after the first dropped input was logged (one warning per terminal).</summary>
    public bool InputDropLogged { get; set; }

    // Not disposed: it has no timer, and the reader thread may still register on its token.
    private readonly CancellationTokenSource _removed = new();

    public void MarkRemoved() => _removed.Cancel();

    public void OnOutput(ReadOnlySpan<byte> data) => _owner.OnOutput(this, data);

    public void OnExited(int exitCode) => _owner.OnExited(this, exitCode);
}
