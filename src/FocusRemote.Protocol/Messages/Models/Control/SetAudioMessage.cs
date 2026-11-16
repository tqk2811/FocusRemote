namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>Control 0x18 SetAudio (V→H): turns audio of the running stream on/off without restarting video.</summary>
public sealed class SetAudioMessage
{
    /// <summary>true = capture + send audio (only if the viewer listed "opus"); false = stop sending.</summary>
    public required bool Enabled { get; init; }
}
