using FocusRemote.Host.Core.Input;
using FocusRemote.Host.Core.Input.Models;

namespace FocusRemote.Host.Tests;

public sealed class KeyCodeMapTests
{
    [Theory]
    [InlineData("KeyA", 0x41, 0x1E, false)]
    [InlineData("KeyZ", 0x5A, 0x2C, false)]
    [InlineData("KeyQ", 0x51, 0x10, false)]
    [InlineData("Digit1", 0x31, 0x02, false)]
    [InlineData("Digit0", 0x30, 0x0B, false)]
    [InlineData("Enter", 0x0D, 0x1C, false)]
    [InlineData("NumpadEnter", 0x0D, 0x1C, true)]
    [InlineData("ControlLeft", 0xA2, 0x1D, false)]
    [InlineData("ControlRight", 0xA3, 0x1D, true)]
    [InlineData("AltRight", 0xA5, 0x38, true)]
    [InlineData("ShiftRight", 0xA1, 0x36, false)]
    [InlineData("ArrowLeft", 0x25, 0x4B, true)]
    [InlineData("Delete", 0x2E, 0x53, true)]
    [InlineData("MetaLeft", 0x5B, 0x5B, true)]
    [InlineData("NumpadDivide", 0x6F, 0x35, true)]
    [InlineData("Numpad7", 0x67, 0x47, false)]
    [InlineData("F1", 0x70, 0x3B, false)]
    [InlineData("F10", 0x79, 0x44, false)]
    [InlineData("F12", 0x7B, 0x58, false)]
    [InlineData("F24", 0x87, 0x76, false)]
    [InlineData("Backquote", 0xC0, 0x29, false)]
    [InlineData("Space", 0x20, 0x39, false)]
    public void KnownCodes_MapToVirtualKeyAndScanCode(string code, int vk, int scan, bool extended)
    {
        Assert.True(KeyCodeMap.TryGet(code, out KeyMapping mapping));
        Assert.Equal(new KeyMapping((ushort)vk, (ushort)scan, extended), mapping);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("keya")]
    [InlineData("Unidentified")]
    public void UnknownCodes_AreRejected(string? code)
    {
        Assert.False(KeyCodeMap.TryGet(code, out _));
    }

    [Fact]
    public void AllLettersAndDigits_AreMappedWithDistinctScanCodes()
    {
        List<ushort> scans = [];
        for (char c = 'A'; c <= 'Z'; c++)
        {
            Assert.True(KeyCodeMap.TryGet("Key" + c, out KeyMapping m));
            Assert.Equal(c, (char)m.VirtualKey);
            scans.Add(m.ScanCode);
        }
        for (char d = '0'; d <= '9'; d++)
        {
            Assert.True(KeyCodeMap.TryGet("Digit" + d, out KeyMapping m));
            scans.Add(m.ScanCode);
        }
        Assert.Equal(scans.Count, scans.Distinct().Count());
    }
}
