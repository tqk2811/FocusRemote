namespace FocusRemote.Host.Core.Sessions.Models;

/// <summary>Plaintext message waiting to be sealed and sent (sealing happens on the writer, in send order).</summary>
internal readonly record struct OutboundMessage(byte Channel, byte Type, byte[] Payload, bool IsVideoFrame);
