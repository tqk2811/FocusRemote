namespace FocusRemote.Host.Core.Terminal.Interfaces;

/// <summary>A running shell attached to a pseudo console. Dispose kills it (and its children) without reporting an exit.</summary>
public interface ITerminalProcess : IDisposable
{
    /// <summary>Queues input bytes; never blocks on a full pipe. Returns false only when the input was dropped because the queue is full; input after the shell ended is discarded and returns true.</summary>
    bool Write(ReadOnlyMemory<byte> data);

    /// <summary>Throws <see cref="System.ComponentModel.Win32Exception"/> when the pseudo console rejects the size.</summary>
    void Resize(int cols, int rows);
}
