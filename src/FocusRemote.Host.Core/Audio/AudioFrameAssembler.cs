namespace FocusRemote.Host.Core.Audio;

/// <summary>
/// Cuts a stream of interleaved float samples into fixed-size encoder frames and keeps the sample clock.
/// WASAPI loopback delivers nothing while no sound plays, so <see cref="FillSilence"/> pads the gap with silent
/// frames to keep packets flowing at the wall-clock rate (the viewer then never starves or bursts).
/// Not thread-safe.
/// </summary>
public sealed class AudioFrameAssembler
{
    private readonly List<float> _pending = [];
    private readonly int _frameSamples;
    private readonly int _channels;
    private readonly int _sampleRate;
    private long _samplesTaken;

    /// <param name="frameSamples">Samples per channel in one frame (960 = 20 ms at 48 kHz).</param>
    public AudioFrameAssembler(int frameSamples, int channels, int sampleRate)
    {
        _frameSamples = frameSamples;
        _channels = channels;
        _sampleRate = sampleRate;
    }

    /// <summary>Buffer that receives new interleaved samples (append to it, e.g. with <see cref="PcmConverter"/>).</summary>
    public List<float> Pending => _pending;

    /// <summary>Samples per channel handed out so far.</summary>
    public long SamplesTaken => _samplesTaken;

    /// <summary>Timestamp (µs since the stream start) of the next frame.</summary>
    public long NextTimestampUs => _samplesTaken * 1_000_000 / _sampleRate;

    /// <summary>Copies the next full frame into <paramref name="frame"/> (length frameSamples × channels).</summary>
    public bool TryTakeFrame(Span<float> frame)
    {
        int count = _frameSamples * _channels;
        if (_pending.Count < count)
            return false;
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending)[..count].CopyTo(frame);
        _pending.RemoveRange(0, count);
        _samplesTaken += _frameSamples;
        return true;
    }

    /// <summary>
    /// When the stream is behind <paramref name="elapsed"/> (wall clock since the start) by at least one frame and no
    /// full frame is pending, pads with silence up to that point. Returns the number of silent samples per channel added.
    /// </summary>
    public int FillSilence(TimeSpan elapsed)
    {
        long dueSamples = (long)(elapsed.TotalSeconds * _sampleRate);
        long pendingSamples = _pending.Count / _channels;
        long missing = dueSamples - _samplesTaken - pendingSamples;
        if (missing < _frameSamples)
            return 0;
        // Whole frames only, so real audio arriving later starts on a frame boundary.
        int add = (int)(missing / _frameSamples * _frameSamples) - (int)(pendingSamples % _frameSamples);
        if (add <= 0)
            return 0;
        _pending.AddRange(new float[add * _channels]);
        return add;
    }
}
