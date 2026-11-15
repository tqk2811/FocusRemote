using System.Collections.Concurrent;
using FocusRemote.Host.Core.Logging.Interfaces;

namespace FocusRemote.Host.Tests.Helpers;

/// <summary>Collects log lines in memory (shown on assertion failure for diagnosis).</summary>
internal sealed class MemoryLog : IAppLog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Info(string message) => Lines.Enqueue("INFO " + message);

    public void Warn(string message, Exception? exception = null) => Lines.Enqueue("WARN " + message + " " + exception);

    public void Error(string message, Exception? exception = null) => Lines.Enqueue("ERROR " + message + " " + exception);

    public override string ToString() => string.Join(Environment.NewLine, Lines);
}
