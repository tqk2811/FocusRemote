using System.Text;
using FocusRemote.Protocol.Crypto;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Handshake;
using FocusRemote.Protocol.Handshake.Enums;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models.Control;

namespace FocusRemote.Protocol.Tests;

public class HandshakeTests
{
    private const int FastIterations = 1000;
    private const string Id = "my-host";

    [Fact]
    public void FullHandshake_Succeeds()
    {
        var host = new HostHandshake(KeyDerivation.DeriveRootKey("good-password", Id, FastIterations));
        var viewer = new ViewerHandshake(KeyDerivation.DeriveRootKey("good-password", Id, FastIterations));

        Assert.True(host.TryHandleHelloViewer(viewer.CreateHelloViewer(), out byte[] helloHost));
        Assert.True(viewer.TryHandleHelloHost(helloHost, out byte[] probe));
        Assert.Equal(ProbeResult.Accepted, host.HandleProbe(probe, out byte[]? fail));
        Assert.Null(fail);
        Assert.Equal(HandshakeState.Completed, host.State);

        using SecureSession hs = host.Session!;
        using SecureSession vs = viewer.Session!;
        byte[] authOk = hs.Seal(Channel.Control, (byte)ControlType.AuthOk,
            ControlMessages.SerializeJson(new AuthOkMessage { HostName = "PC", Version = "1.0.0" }));
        Assert.True(vs.TryOpen(authOk, out byte c, out byte t, out byte[] p));
        Assert.Equal((byte)ControlType.AuthOk, t);
        Assert.Equal((byte)Channel.Control, c);
        Assert.Equal("""{"hostName":"PC","version":"1.0.0"}""", Encoding.UTF8.GetString(p));

        byte[] caps = vs.Seal(Channel.Control, (byte)ControlType.Capabilities, [0x7b, 0x7d]);
        Assert.True(hs.TryOpen(caps, out _, out t, out _));
        Assert.Equal((byte)ControlType.Capabilities, t);
    }

    [Fact]
    public void WrongPassword_ProbeRejected()
    {
        var host = new HostHandshake(KeyDerivation.DeriveRootKey("good-password", Id, FastIterations));
        var viewer = new ViewerHandshake(KeyDerivation.DeriveRootKey("bad-password!", Id, FastIterations));

        Assert.True(host.TryHandleHelloViewer(viewer.CreateHelloViewer(), out byte[] helloHost));
        Assert.True(viewer.TryHandleHelloHost(helloHost, out byte[] probe));
        Assert.Equal(ProbeResult.Rejected, host.HandleProbe(probe, out byte[]? fail));
        Assert.True(HandshakeFrames.IsAuthFail(fail));
        Assert.Null(host.Session);
        Assert.Equal(HandshakeState.Failed, host.State);
    }

    [Fact]
    public void ProbeWithWrongPayload_Rejected()
    {
        byte[] root = KeyDerivation.DeriveRootKey("good-password", Id, FastIterations);
        var host = new HostHandshake(root);
        byte[] nonceV = new byte[32];
        Assert.True(host.TryHandleHelloViewer(HandshakeFrames.BuildHello(FrameKind.HelloViewer, nonceV), out byte[] hh));
        Assert.True(HandshakeFrames.TryParseHello(hh, FrameKind.HelloHost, out byte[] nonceH));
        using var fake = new SecureSession(KeyDerivation.DeriveSessionKeys(root, nonceV, nonceH), SessionRole.Viewer);
        Assert.Equal(ProbeResult.Rejected, host.HandleProbe(fake.Seal(Channel.Control, (byte)ControlType.AuthProbe, "wrong"u8), out _));
    }

    [Fact]
    public void Host_IgnoresOutOfOrderFrames()
    {
        var host = new HostHandshake(KeyDerivation.DeriveRootKey("good-password", Id, FastIterations));
        Assert.Equal(ProbeResult.Ignored, host.HandleProbe([0x10, 0, 0], out _));
        Assert.False(host.TryHandleHelloViewer([0x01, 2, 0], out _));
        Assert.False(host.TryHandleHelloViewer(HandshakeFrames.BuildHello(FrameKind.HelloHost, new byte[32]), out _));
        Assert.True(host.TryHandleHelloViewer(HandshakeFrames.BuildHello(FrameKind.HelloViewer, new byte[32]), out _));
        Assert.Equal(ProbeResult.Ignored, host.HandleProbe(HandshakeFrames.BuildHello(FrameKind.HelloViewer, new byte[32]), out _));
    }

    [Fact]
    public void DelayPolicy_DoublesAndCaps()
    {
        var time = new ManualTimeProvider();
        var policy = new AuthFailureDelayPolicy(time);
        Assert.Equal(TimeSpan.Zero, policy.GetRemainingDelay());
        int[] expected = [1, 2, 4, 8, 16, 30, 30, 30];
        foreach (int s in expected)
            Assert.Equal(TimeSpan.FromSeconds(s), policy.RegisterFailure());
        Assert.Equal(TimeSpan.FromSeconds(30), policy.GetRemainingDelay());
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.GetRemainingDelay());
        policy.RegisterSuccess();
        Assert.Equal(TimeSpan.Zero, policy.GetRemainingDelay());
        Assert.Equal(TimeSpan.FromSeconds(1), policy.RegisterFailure());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
