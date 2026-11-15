using System.Globalization;
using FocusRemote.Host.Core.Logging.Interfaces;

namespace FocusRemote.Host.Core.Logging;

/// <summary>
/// Appends lines to <c>&lt;directory&gt;/host-yyyyMMdd.log</c> synchronously (so a crash handler's last line
/// reaches the disk before the process dies). Thread-safe; never throws.
/// </summary>
public sealed class FileAppLog : IAppLog
{
    private readonly string _directory;
    private readonly object _lock = new();

    public FileAppLog(string directory)
    {
        _directory = directory;
    }

    public string Directory => _directory;

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            DateTime now = DateTime.Now;
            string line = $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] [{Environment.CurrentManagedThreadId}] {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            lock (_lock)
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, $"host-{now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            System.Diagnostics.Debug.WriteLine(line);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
