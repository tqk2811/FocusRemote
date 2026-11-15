namespace FocusRemote.Host.Core.Logging.Interfaces;

/// <summary>Minimal application log. Implementations must never throw.</summary>
public interface IAppLog
{
    void Info(string message);

    void Warn(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}
