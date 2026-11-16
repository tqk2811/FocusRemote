using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Helpers;
using FocusRemote.Protocol.Messages;
using FocusRemote.Protocol.Messages.Models;
using FocusRemote.Protocol.Messages.Models.Control;
using FocusRemote.Protocol.Messages.Models.Input;
using FocusRemote.Protocol.Messages.Models.Terminal;

namespace FocusRemote.Protocol.Tests;

public class MessageCodecTests
{
    [Fact]
    public void SetInputMode_RoundTripsAndRejectsMissingMode()
    {
        byte[] json = ControlMessages.SerializeJson(new SetInputModeMessage { Mode = SetInputModeMessage.Message });
        Assert.Equal("""{"mode":"message"}""", System.Text.Encoding.UTF8.GetString(json));
        Assert.True(ControlMessages.TryDeserializeJson(json, out SetInputModeMessage? back));
        Assert.Equal("message", back!.Mode);
        Assert.Equal(0x17, (byte)ControlType.SetInputMode);
        Assert.False(ControlMessages.TryDeserializeJson("""{"other":1}"""u8, out SetInputModeMessage? _));
    }

    [Fact]
    public void Video_Roundtrip()
    {
        byte[] p = VideoMessages.BuildFrame(true, 123456789UL, [0, 0, 0, 1, 0x65]);
        Assert.Equal("0115cd5b07000000000000000165", Convert.ToHexString(p).ToLowerInvariant());
        Assert.True(VideoMessages.TryParseFrame(p, out VideoFrameMessage? m));
        Assert.True(m!.IsKeyframe);
        Assert.Equal(123456789UL, m.TimestampUs);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65 }, m.Data.ToArray());
        Assert.False(VideoMessages.TryParseFrame(new byte[8], out _));
    }

    [Fact]
    public void Audio_Roundtrip()
    {
        Assert.True(AudioMessages.TryParsePacket(AudioMessages.BuildPacket(42, [7, 8]), out AudioPacketMessage? pkt));
        Assert.Equal(42UL, pkt!.TimestampUs);
        Assert.Equal(new byte[] { 7, 8 }, pkt.Data.ToArray());
        byte[] cfg = AudioMessages.BuildConfig(new AudioConfigMessage { Codec = "opus", SampleRate = 48000, Channels = 2 });
        Assert.Equal("""{"codec":"opus","sampleRate":48000,"channels":2}""", System.Text.Encoding.UTF8.GetString(cfg));
        Assert.True(AudioMessages.TryParseConfig(cfg, out AudioConfigMessage? c));
        Assert.Equal(48000, c!.SampleRate);
    }

    [Fact]
    public void Ping_Roundtrip()
    {
        Assert.True(ControlMessages.TryParsePing(ControlMessages.BuildPing(ulong.MaxValue - 1), out ulong ts));
        Assert.Equal(ulong.MaxValue - 1, ts);
        Assert.False(ControlMessages.TryParsePing(new byte[7], out _));
    }

    public static TheoryData<InputMessage, string> InputCases => new()
    {
        { new MouseMoveInput { X = 1, Y = 65535 }, "0100ffff" },
        { new MouseButtonInput { Button = MouseButton.X2, Down = true, X = 2, Y = 3 }, "040102000300" },
        { new MouseWheelInput { DeltaX = -120, DeltaY = 240, X = 0, Y = 0 }, "88fff00000000000" },
        { new KeyInput { Down = true, Code = "KeyA" }, "01044b657941" },
        { new TextInput { Text = "Việt" }, "5669e1bb8774" },
        { new ReleaseAllInput(), "" },
    };

    [Theory]
    [MemberData(nameof(InputCases))]
    public void Input_Roundtrip(InputMessage message, string hex)
    {
        byte[] p = InputMessages.Build(message);
        Assert.Equal(hex, Convert.ToHexString(p).ToLowerInvariant());
        Assert.True(InputMessages.TryParse((byte)message.Type, p, out InputMessage? parsed));
        Assert.Equal(message.GetType(), parsed!.GetType());
        Assert.Equal(p, InputMessages.Build(parsed));
    }

    [Fact]
    public void Input_UnknownOrShort_ReturnsFalse()
    {
        Assert.False(InputMessages.TryParse(0x7F, [1, 2, 3, 4], out _));
        Assert.False(InputMessages.TryParse((byte)InputType.MouseMove, [1, 2, 3], out _));
        Assert.False(InputMessages.TryParse((byte)InputType.Key, [1, 5, 0x41], out _));
    }

    [Fact]
    public void ControlJson_Roundtrip_CamelCase()
    {
        var list = new SourceListMessage
        {
            Sources = [new SourceInfo { Id = "w:1234", Kind = "window", Title = "Notepad", Width = 800, Height = 600 }],
        };
        byte[] json = ControlMessages.SerializeJson(list);
        Assert.Equal("""{"sources":[{"id":"w:1234","kind":"window","title":"Notepad","width":800,"height":600}]}""", System.Text.Encoding.UTF8.GetString(json));
        Assert.True(ControlMessages.TryDeserializeJson(json, out SourceListMessage? back));
        Assert.Equal("Notepad", back!.Sources[0].Title);

        Assert.True(ControlMessages.TryDeserializeJson("""{"id":"m:0","maxWidth":1920,"maxHeight":1080,"maxFps":30,"bitrateKbps":4000,"audio":true,"future":1}"""u8, out SelectSourceMessage? sel));
        Assert.True(sel!.Audio);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(ControlMessages.SerializeEmpty()));
    }

    [Fact]
    public void ControlJson_Malformed_ReturnsFalse()
    {
        Assert.False(ControlMessages.TryDeserializeJson("not json"u8, out AuthOkMessage? _));
        Assert.False(ControlMessages.TryDeserializeJson("""{"hostName":"PC"}"""u8, out AuthOkMessage? _));
        Assert.False(ControlMessages.TryDeserializeJson("null"u8, out AuthOkMessage? _));
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("A_b-9", true)]
    [InlineData("ab", false)]
    [InlineData("has space", false)]
    [InlineData("việt", false)]
    [InlineData(null, false)]
    public void IdValidation(string? id, bool valid) => Assert.Equal(valid, CredentialValidator.IsValidId(id));

    [Fact]
    public void IdValidation_Length()
    {
        Assert.True(CredentialValidator.IsValidId(new string('a', 64)));
        Assert.False(CredentialValidator.IsValidId(new string('a', 65)));
        Assert.False(CredentialValidator.IsValidId("abc\n"));
    }

    [Theory]
    [InlineData("123456789", false)]
    [InlineData("1234567890", true)]
    [InlineData(null, false)]
    public void PasswordValidation(string? pw, bool valid) => Assert.Equal(valid, CredentialValidator.IsValidPassword(pw));

    [Fact]
    public void Terminal_BinaryPayloads()
    {
        byte[] data = TerminalMessages.BuildData(0x01020304, [0x6c, 0x73, 0x0d]);
        Assert.Equal("040302016c730d", Convert.ToHexString(data).ToLowerInvariant());
        Assert.True(TerminalMessages.TryParseData(data, out TerminalDataMessage? d));
        Assert.Equal(0x01020304u, d!.TerminalId);
        Assert.Equal(new byte[] { 0x6c, 0x73, 0x0d }, d.Data.ToArray());
        Assert.True(TerminalMessages.TryParseData(new byte[4], out TerminalDataMessage? empty));
        Assert.Equal(0, empty!.Data.Length);
        Assert.False(TerminalMessages.TryParseData(new byte[3], out _));

        byte[] resize = TerminalMessages.BuildResize(7, 120, 30);
        Assert.Equal("0700000078001e00", Convert.ToHexString(resize).ToLowerInvariant());
        Assert.True(TerminalMessages.TryParseResize(resize, out TerminalResizeMessage? r));
        Assert.Equal((7u, (ushort)120, (ushort)30), (r!.TerminalId, r.Cols, r.Rows));
        Assert.False(TerminalMessages.TryParseResize(TerminalMessages.BuildResize(7, 0, 30), out _));
        Assert.False(TerminalMessages.TryParseResize(new byte[7], out _));

        Assert.True(TerminalMessages.TryParseClose(TerminalMessages.BuildClose(9), out uint id));
        Assert.Equal(9u, id);
        Assert.False(TerminalMessages.TryParseClose(new byte[3], out _));
    }

    [Fact]
    public void Terminal_Json()
    {
        byte[] list = ControlMessages.SerializeJson(new ShellListMessage { Enabled = true, Shells = [new ShellInfo { Id = "cmd", Name = "Command Prompt" }] });
        Assert.Equal("""{"enabled":true,"shells":[{"id":"cmd","name":"Command Prompt"}]}""", System.Text.Encoding.UTF8.GetString(list));
        Assert.True(ControlMessages.TryDeserializeJson("""{"terminalId":3,"shellId":"pwsh","cols":80,"rows":24}"""u8, out TerminalOpenMessage? open));
        Assert.Equal((3u, "pwsh", 80, 24), (open!.TerminalId, open.ShellId, open.Cols, open.Rows));
        Assert.False(ControlMessages.TryDeserializeJson("""{"terminalId":3,"cols":80,"rows":24}"""u8, out TerminalOpenMessage? _));
        byte[] closed = ControlMessages.SerializeJson(new TerminalClosedMessage { TerminalId = 3, Reason = "exited", ExitCode = 0 });
        Assert.Equal("""{"terminalId":3,"reason":"exited","exitCode":0,"message":null}""", System.Text.Encoding.UTF8.GetString(closed));
    }
}
