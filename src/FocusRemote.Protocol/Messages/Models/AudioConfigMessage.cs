namespace FocusRemote.Protocol.Messages.Models;

/// <summary>Audio/Config JSON: <c>{"codec":"opus","sampleRate":48000,"channels":2}</c>.</summary>
public sealed class AudioConfigMessage
{
    public required string Codec { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
}
