namespace FocusRemote.Protocol.Crypto.Models;

/// <summary>The two direction keys derived for one session (32 bytes each).</summary>
public sealed class SessionKeys
{
    /// <summary>Key for frames sent host → viewer (okm[0..32]).</summary>
    public required byte[] KeyH2V { get; init; }

    /// <summary>Key for frames sent viewer → host (okm[32..64]).</summary>
    public required byte[] KeyV2H { get; init; }
}
