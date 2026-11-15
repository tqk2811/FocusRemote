namespace FocusRemote.Protocol.Handshake;

/// <summary>
/// Host-side brute-force throttle (protocol §4 step 5): after n consecutive failures the host waits
/// min(2^(n-1), 30) seconds before answering the next HelloViewer. Thread-safe. Time is injectable.
/// </summary>
public sealed class AuthFailureDelayPolicy
{
    private static readonly TimeSpan _baseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _maxDelay = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public AuthFailureDelayPolicy(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int ConsecutiveFailures { get { lock (_lock) return _consecutiveFailures; } }

    /// <summary>Records a failure and returns the delay now in force.</summary>
    public TimeSpan RegisterFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            TimeSpan delay = GetDelayFor(_consecutiveFailures);
            _blockedUntil = _timeProvider.GetUtcNow() + delay;
            return delay;
        }
    }

    /// <summary>Resets after a successful login.</summary>
    public void RegisterSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _blockedUntil = DateTimeOffset.MinValue;
        }
    }

    /// <summary>How long the host should still wait before processing the next attempt (zero if none).</summary>
    public TimeSpan GetRemainingDelay()
    {
        lock (_lock)
        {
            TimeSpan remaining = _blockedUntil - _timeProvider.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Delay for the given number of consecutive failures: 1s, 2s, 4s, … capped at 30s.</summary>
    public static TimeSpan GetDelayFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return TimeSpan.Zero;
        if (consecutiveFailures > 6)
            return _maxDelay;
        TimeSpan delay = _baseDelay * (1 << (consecutiveFailures - 1));
        return delay < _maxDelay ? delay : _maxDelay;
    }
}
