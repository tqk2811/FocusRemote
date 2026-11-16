using FocusRemote.Host.Core.Input;
using FocusRemote.Host.Core.Input.Enums;
using FocusRemote.Host.Core.Input.Models;
using FocusRemote.Host.Core.Sessions;
using FocusRemote.Protocol.Enums;
using static FocusRemote.Host.Core.Input.WindowMessageBuilder;

namespace FocusRemote.Host.Tests;

public sealed class MessageInputTests
{
    [Theory]
    [InlineData(10, 20, 0x0014000A)]
    [InlineData(-1, 5, 0x0005FFFF)]
    [InlineData(3, -2, unchecked((int)0xFFFE0003))]
    public void PointLParam_PacksSignedWords(int x, int y, int expected)
        => Assert.Equal((nint)expected, PointLParam(x, y));

    [Fact]
    public void WParam_WheelDeltaInHighWord()
    {
        Assert.Equal((nint)0x00780001, WParam(MK_LBUTTON, 120));
        Assert.Equal(unchecked((nint)(int)0xFF880000), WParam(0, -120));
    }

    [Theory]
    [InlineData(MouseButton.Left, true, WM_LBUTTONDOWN, 0)]
    [InlineData(MouseButton.Right, false, WM_RBUTTONUP, 0)]
    [InlineData(MouseButton.Middle, true, WM_MBUTTONDOWN, 0)]
    [InlineData(MouseButton.X1, true, WM_XBUTTONDOWN, 1)]
    [InlineData(MouseButton.X2, false, WM_XBUTTONUP, 2)]
    public void ButtonMessage_MapsButtons(MouseButton button, bool down, uint msg, ushort xButton)
        => Assert.Equal((msg, xButton), ButtonMessage(button, down));

    [Fact]
    public void KeyLParam_SetsRepeatScanExtendedAndTransitionBits()
    {
        KeyMapping a = new(0x41, 0x1E, false);
        Assert.Equal((nint)0x001E0001, KeyLParam(a, down: true, wasDown: false, contextAlt: false));
        Assert.Equal((nint)0x401E0001, KeyLParam(a, down: true, wasDown: true, contextAlt: false));
        Assert.Equal(unchecked((nint)(int)0xC01E0001), KeyLParam(a, down: false, wasDown: true, contextAlt: false));

        KeyMapping right = new(0x27, 0x4D, true);
        Assert.Equal((nint)0x214D0001, KeyLParam(right, down: true, wasDown: false, contextAlt: true));
    }

    [Theory]
    [InlineData(true, false, false, WM_KEYDOWN)]
    [InlineData(false, false, false, WM_KEYUP)]
    [InlineData(true, true, false, WM_SYSKEYDOWN)]
    [InlineData(false, true, false, WM_SYSKEYUP)]
    [InlineData(true, true, true, WM_KEYDOWN)]
    public void KeyMessage_UsesSysVariantWhenAltWithoutCtrl(bool down, bool alt, bool ctrl, uint expected)
        => Assert.Equal(expected, KeyMessage(down, alt, ctrl));

    [Fact]
    public void State_TracksModifiersButtonsAndRepeat()
    {
        MessageInputState state = new();
        Assert.False(state.SetKey("ShiftLeft", true));
        Assert.True(state.SetKey("ShiftLeft", true)); // auto-repeat: was already down
        state.SetKey("ControlRight", true);
        state.SetButton(MouseButton.Left, true);
        state.SetButton(MouseButton.X2, true);
        Assert.Equal(MK_SHIFT | MK_CONTROL | MK_LBUTTON | MK_XBUTTON2, state.MouseKeyFlags);
        Assert.False(state.AltHeld);
        state.SetKey("AltLeft", true);
        Assert.True(state.AltHeld);

        Assert.True(state.SetKey("ShiftLeft", false));
        state.SetButton(MouseButton.Left, false);
        Assert.Equal(MK_CONTROL | MK_XBUTTON2, state.MouseKeyFlags);
        Assert.True(state.AnyButtonHeld);

        state.Clear();
        Assert.Empty(state.HeldKeys);
        Assert.Empty(state.HeldButtons);
        Assert.Equal(0, state.MouseKeyFlags);
    }

    [Theory]
    [InlineData("message", InputMode.Message)]
    [InlineData("MESSAGE", InputMode.Message)]
    [InlineData("sendinput", InputMode.SendInput)]
    [InlineData("bogus", InputMode.SendInput)]
    [InlineData(null, InputMode.SendInput)]
    public void ParseInputMode_DefaultsToSendInput(string? value, InputMode expected)
        => Assert.Equal(expected, ViewerSession.ParseInputMode(value));

    private static readonly IntPtr Hwnd = new(0x1234);

    [Fact]
    public void IsDoubleClick_RequiresSameButtonWindowTimeAndDistance()
    {
        ClickRecord first = new(MouseButton.Left, Hwnd, 1000, 100, 100);
        Assert.False(IsDoubleClick(null, first, 500, 4, 4));
        Assert.True(IsDoubleClick(first, first with { TimeMs = 1500, X = 102, Y = 98 }, 500, 4, 4));
        Assert.False(IsDoubleClick(first, first with { TimeMs = 1501 }, 500, 4, 4));
        Assert.False(IsDoubleClick(first, first with { TimeMs = 1100, X = 103 }, 500, 4, 4));
        Assert.False(IsDoubleClick(first, first with { TimeMs = 1100, Button = MouseButton.Right }, 500, 4, 4));
        Assert.False(IsDoubleClick(first, first with { TimeMs = 1100, Window = new IntPtr(0x99) }, 500, 4, 4));
        Assert.False(IsDoubleClick(first, first with { TimeMs = 900 }, 500, 4, 4));
    }

    [Theory]
    [InlineData(WM_LBUTTONDOWN, WM_LBUTTONDBLCLK)]
    [InlineData(WM_XBUTTONDOWN, WM_XBUTTONDBLCLK)]
    [InlineData(WM_NCRBUTTONDOWN, WM_NCRBUTTONDBLCLK)]
    [InlineData(WM_LBUTTONUP, 0u)]
    public void DoubleClickMessage_MapsDowns(uint down, uint expected) => Assert.Equal(expected, DoubleClickMessage(down));

    [Theory]
    [InlineData(WM_MOUSEMOVE, WM_NCMOUSEMOVE)]
    [InlineData(WM_LBUTTONDOWN, WM_NCLBUTTONDOWN)]
    [InlineData(WM_RBUTTONUP, WM_NCRBUTTONUP)]
    [InlineData(WM_XBUTTONDOWN, WM_NCXBUTTONDOWN)]
    [InlineData(WM_MOUSEWHEEL, 0u)]
    public void NonClientMessage_MapsClientMessages(uint client, uint expected) => Assert.Equal(expected, NonClientMessage(client));

    [Theory]
    [InlineData(1, false)]  // HTCLIENT
    [InlineData(0, false)]  // HTNOWHERE
    [InlineData(-1, false)] // HTTRANSPARENT
    [InlineData(-2, false)] // HTERROR
    [InlineData(2, true)]   // HTCAPTION
    [InlineData(20, true)]  // HTCLOSE
    public void IsNonClientHit_ExcludesClientAndNowhere(int hit, bool expected) => Assert.Equal(expected, IsNonClientHit(hit));

    [Theory]
    [InlineData(WM_NCLBUTTONDOWN, 2, true)]    // caption drag
    [InlineData(WM_NCLBUTTONDOWN, 17, true)]   // bottom-right sizing
    [InlineData(WM_NCLBUTTONDOWN, 20, false)]  // HTCLOSE
    [InlineData(WM_NCLBUTTONDBLCLK, 2, false)] // caption double-click (maximize)
    [InlineData(WM_NCRBUTTONDOWN, 2, false)]
    public void StartsModalMoveSize_OnlyLeftDownOnCaptionOrBorder(uint msg, int hit, bool expected)
        => Assert.Equal(expected, StartsModalMoveSize(msg, hit));

    [Theory]
    [InlineData(0x41, true, false, true, (int)0x01)]   // Ctrl+A -> 0x01
    [InlineData(0x5A, true, false, true, (int)0x1A)]   // Ctrl+Z -> 0x1A
    [InlineData(0x43, true, true, true, -1)]           // Ctrl+Alt+C: suppressed, no char
    [InlineData(0x31, false, true, true, -1)]          // Alt+1: suppressed
    [InlineData(0x31, true, false, true, -1)]          // Ctrl+1: suppressed, digits have no control char
    [InlineData(0x41, false, false, false, -1)]        // plain A: posted normally
    [InlineData(0x0D, true, false, false, -1)]         // Ctrl+Enter: posted normally
    public void ChordHelpers_SuppressLetterDigitShortcuts(int vk, bool ctrl, bool alt, bool suppressed, int controlChar)
    {
        Assert.Equal(suppressed, IsSuppressedChordKey((ushort)vk, ctrl, alt));
        char? expected = controlChar < 0 ? null : (char)controlChar;
        Assert.Equal(expected, ControlChar((ushort)vk, ctrl, alt));
    }
}
