namespace FocusRemote.Host.Core.Terminal.Interfaces;

/// <summary>Callbacks of one <see cref="ITerminalProcess"/>, invoked on its background threads.</summary>
public interface ITerminalProcessListener
{
    /// <summary>Output bytes; the span is only valid during the call. May block (backpressure).</summary>
    void OnOutput(ReadOnlySpan<byte> data);

    /// <summary>The shell exited by itself; all output was delivered before this call. Not raised after Dispose.</summary>
    void OnExited(int exitCode);
}
