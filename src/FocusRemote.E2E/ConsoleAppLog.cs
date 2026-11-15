using System.Globalization;
using FocusRemote.Host.Core.Logging.Interfaces;

namespace FocusRemote.E2E;

/// <summary>Writes log lines to stdout (flushed per line). Thread-safe; never throws.</summary>
internal sealed class ConsoleAppLog : IAppLog
{
    private readonly object _lock = new();

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            string line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            lock (_lock)
            {
                Console.Out.WriteLine(line);
                Console.Out.Flush();
            }
        }
        catch
        {
            // Logging must never take the harness down.
        }
    }
}
