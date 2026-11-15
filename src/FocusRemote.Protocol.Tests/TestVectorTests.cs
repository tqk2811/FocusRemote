using System.Text.Json;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Crypto.Models;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Handshake;

namespace FocusRemote.Protocol.Tests;

/// <summary>The C# implementation must reproduce TestVectors.json (shared with the JS WebViewer).</summary>
public class TestVectorTests
{
    private static readonly JsonElement _v = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestVectors.json"))).RootElement;

    private static string S(string name) => _v.GetProperty(name).GetString()!;
    private static byte[] H(string name) => Convert.FromHexString(S(name));
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static SessionKeys Keys => new() { KeyH2V = H("keyH2VHex"), KeyV2H = H("keyV2HHex") };

    [Fact]
    public void KeyDerivation_MatchesVectors()
    {
        Assert.Equal(S("saltHex"), Hex(KeyDerivation.DeriveSalt(S("id"))));
        byte[] root = KeyDerivation.DeriveRootKey(S("password"), S("id"), _v.GetProperty("pbkdf2Iterations").GetInt32());
        Assert.Equal(ProtocolConstants.Pbkdf2Iterations, _v.GetProperty("pbkdf2Iterations").GetInt32());
        Assert.Equal(S("rootKeyHex"), Hex(root));
        SessionKeys keys = KeyDerivation.DeriveSessionKeys(root, H("nonceVHex"), H("nonceHHex"));
        Assert.Equal(S("keyH2VHex"), Hex(keys.KeyH2V));
        Assert.Equal(S("keyV2HHex"), Hex(keys.KeyV2H));
    }

    [Fact]
    public void HelloFrames_MatchVectors()
    {
        Assert.Equal(S("helloViewerHex"), Hex(HandshakeFrames.BuildHello(FrameKind.HelloViewer, H("nonceVHex"))));
        Assert.Equal(S("helloHostHex"), Hex(HandshakeFrames.BuildHello(FrameKind.HelloHost, H("nonceHHex"))));
    }

    [Theory]
    [InlineData("sealedV2H", SessionRole.Viewer, SessionRole.Host)]
    [InlineData("sealedH2V", SessionRole.Host, SessionRole.Viewer)]
    public void SealedFrames_MatchVectors(string list, SessionRole senderRole, SessionRole receiverRole)
    {
        using var sender = new SecureSession(Keys, senderRole);
        using var receiver = new SecureSession(Keys, receiverRole);
        foreach (JsonElement item in _v.GetProperty(list).EnumerateArray())
        {
            Assert.Equal(item.GetProperty("counter").GetUInt64(), sender.NextSendCounter);
            byte channel = item.GetProperty("channel").GetByte();
            byte type = item.GetProperty("type").GetByte();
            byte[] payload = Convert.FromHexString(item.GetProperty("payloadHex").GetString()!);
            string frameHex = item.GetProperty("frameHex").GetString()!;

            Assert.Equal(frameHex, Hex(sender.Seal(channel, type, payload)));

            Assert.True(receiver.TryOpen(Convert.FromHexString(frameHex), out byte c, out byte t, out byte[] p));
            Assert.Equal(channel, c);
            Assert.Equal(type, t);
            Assert.Equal(payload, p);
        }
    }
}
