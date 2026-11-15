using System.Security.Cryptography;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Crypto.Models;
using FocusRemote.Protocol.Enums;

namespace FocusRemote.Protocol.Tests;

public class SecureSessionTests
{
    private const int FastIterations = 1000;

    private static SessionKeys NewKeys(string password = "password1234")
        => KeyDerivation.DeriveSessionKeys(
            KeyDerivation.DeriveRootKey(password, "host01", FastIterations),
            new byte[32], Enumerable.Repeat((byte)1, 32).ToArray());

    [Fact]
    public void KeyDerivation_IsDeterministic()
    {
        Assert.Equal(KeyDerivation.DeriveRootKey("password1234", "abc", FastIterations), KeyDerivation.DeriveRootKey("password1234", "abc", FastIterations));
        Assert.NotEqual(KeyDerivation.DeriveRootKey("password1234", "abc", FastIterations), KeyDerivation.DeriveRootKey("password1234", "abd", FastIterations));
        SessionKeys a = NewKeys(), b = NewKeys();
        Assert.Equal(a.KeyH2V, b.KeyH2V);
        Assert.Equal(a.KeyV2H, b.KeyV2H);
        Assert.NotEqual(a.KeyH2V, a.KeyV2H);
    }

    [Theory]
    [InlineData(SessionRole.Host, SessionRole.Viewer)]
    [InlineData(SessionRole.Viewer, SessionRole.Host)]
    public void SealOpen_Roundtrip(SessionRole from, SessionRole to)
    {
        SessionKeys keys = NewKeys();
        using var sender = new SecureSession(keys, from);
        using var receiver = new SecureSession(keys, to);
        for (int i = 0; i < 3; i++)
        {
            byte[] payload = RandomNumberGenerator.GetBytes(i * 100);
            byte[] frame = sender.Seal(Channel.Video, 0x01, payload);
            Assert.True(receiver.TryOpen(frame, out byte c, out byte t, out byte[] p));
            Assert.Equal((byte)Channel.Video, c);
            Assert.Equal(1, t);
            Assert.Equal(payload, p);
        }
    }

    [Fact]
    public void SameDirectionKey_CannotOpenOwnFrames()
    {
        SessionKeys keys = NewKeys();
        using var host = new SecureSession(keys, SessionRole.Host);
        using var host2 = new SecureSession(keys, SessionRole.Host);
        Assert.False(host2.TryOpen(host.Seal(Channel.Control, 1, [1, 2, 3]), out _, out _, out _));
    }

    [Fact]
    public void Tampered_IsRejected()
    {
        SessionKeys keys = NewKeys();
        using var sender = new SecureSession(keys, SessionRole.Host);
        byte[] frame = sender.Seal(Channel.Control, 2, [1, 2, 3, 4]);
        for (int i = 1; i < frame.Length; i++)
        {
            using var receiver = new SecureSession(keys, SessionRole.Viewer);
            byte[] bad = (byte[])frame.Clone();
            bad[i] ^= 0x01;
            Assert.False(receiver.TryOpen(bad, out _, out _, out _));
        }
        using var ok = new SecureSession(keys, SessionRole.Viewer);
        Assert.False(ok.TryOpen(frame.AsSpan(0, frame.Length - 1), out _, out _, out _));
        Assert.True(ok.TryOpen(frame, out _, out _, out _));
    }

    [Fact]
    public void Replay_IsRejected()
    {
        SessionKeys keys = NewKeys();
        using var sender = new SecureSession(keys, SessionRole.Viewer);
        using var receiver = new SecureSession(keys, SessionRole.Host);
        byte[] f0 = sender.Seal(Channel.Input, 1, [0, 0, 0, 0]);
        byte[] f1 = sender.Seal(Channel.Input, 1, [0, 0, 0, 0]);
        byte[] f2 = sender.Seal(Channel.Input, 1, [0, 0, 0, 0]);
        Assert.True(receiver.TryOpen(f0, out _, out _, out _));
        Assert.False(receiver.TryOpen(f0, out _, out _, out _));
        Assert.True(receiver.TryOpen(f2, out _, out _, out _));
        Assert.False(receiver.TryOpen(f1, out _, out _, out _));
    }

    [Fact]
    public void UnknownChannel_OpensWithoutThrowing()
    {
        SessionKeys keys = NewKeys();
        using var sender = new SecureSession(keys, SessionRole.Host);
        using var receiver = new SecureSession(keys, SessionRole.Viewer);
        Assert.True(receiver.TryOpen(sender.Seal(200, 0xEE, [9]), out byte c, out byte t, out _));
        Assert.False(Enum.IsDefined((Channel)c));
        Assert.Equal(0xEE, t);
    }
}
