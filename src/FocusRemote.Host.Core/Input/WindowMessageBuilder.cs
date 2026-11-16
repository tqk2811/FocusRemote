using FocusRemote.Host.Core.Input.Models;
using FocusRemote.Protocol.Enums;

namespace FocusRemote.Host.Core.Input;

/// <summary>Pure helpers that build the message id / wParam / lParam of posted mouse and keyboard window messages.</summary>
internal static class WindowMessageBuilder
{
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCMOUSEMOVE = 0x00A0;
    public const uint WM_NCLBUTTONDOWN = 0x00A1;
    public const uint WM_NCLBUTTONUP = 0x00A2;
    public const uint WM_NCLBUTTONDBLCLK = 0x00A3;
    public const uint WM_NCRBUTTONDOWN = 0x00A4;
    public const uint WM_NCRBUTTONUP = 0x00A5;
    public const uint WM_NCRBUTTONDBLCLK = 0x00A6;
    public const uint WM_NCMBUTTONDOWN = 0x00A7;
    public const uint WM_NCMBUTTONUP = 0x00A8;
    public const uint WM_NCMBUTTONDBLCLK = 0x00A9;
    public const uint WM_NCXBUTTONDOWN = 0x00AB;
    public const uint WM_NCXBUTTONUP = 0x00AC;
    public const uint WM_NCXBUTTONDBLCLK = 0x00AD;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_RBUTTONDBLCLK = 0x0206;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MBUTTONDBLCLK = 0x0209;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_XBUTTONDOWN = 0x020B;
    public const uint WM_XBUTTONUP = 0x020C;
    public const uint WM_XBUTTONDBLCLK = 0x020D;
    public const uint WM_MOUSEHWHEEL = 0x020E;

    public const int MK_LBUTTON = 0x0001;
    public const int MK_RBUTTON = 0x0002;
    public const int MK_SHIFT = 0x0004;
    public const int MK_CONTROL = 0x0008;
    public const int MK_MBUTTON = 0x0010;
    public const int MK_XBUTTON1 = 0x0020;
    public const int MK_XBUTTON2 = 0x0040;

    public const int HTERROR = -2;
    public const int HTTRANSPARENT = -1;
    public const int HTNOWHERE = 0;
    public const int HTCLIENT = 1;

    public const ushort XBUTTON1 = 0x0001;
    public const ushort XBUTTON2 = 0x0002;

    /// <summary>MAKELPARAM(x, y): low word x, high word y, each as a signed 16-bit value (negative client coordinates allowed).</summary>
    public static nint PointLParam(int x, int y) => (nint)(int)(((uint)(ushort)(short)y << 16) | (ushort)(short)x);

    /// <summary>MAKEWPARAM(low, high).</summary>
    public static nint WParam(int low, int high) => (nint)(int)(((uint)(ushort)high << 16) | (ushort)low);

    /// <summary>MK_* flag of a mouse button.</summary>
    public static int ButtonFlag(MouseButton button) => button switch
    {
        MouseButton.Left => MK_LBUTTON,
        MouseButton.Right => MK_RBUTTON,
        MouseButton.Middle => MK_MBUTTON,
        MouseButton.X1 => MK_XBUTTON1,
        MouseButton.X2 => MK_XBUTTON2,
        _ => 0,
    };

    /// <summary>Message id and XBUTTON high word (0 for non-X buttons) of a button press/release.</summary>
    public static (uint Message, ushort XButton) ButtonMessage(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => (down ? WM_LBUTTONDOWN : WM_LBUTTONUP, (ushort)0),
        MouseButton.Right => (down ? WM_RBUTTONDOWN : WM_RBUTTONUP, (ushort)0),
        MouseButton.Middle => (down ? WM_MBUTTONDOWN : WM_MBUTTONUP, (ushort)0),
        MouseButton.X1 => (down ? WM_XBUTTONDOWN : WM_XBUTTONUP, XBUTTON1),
        MouseButton.X2 => (down ? WM_XBUTTONDOWN : WM_XBUTTONUP, XBUTTON2),
        _ => (0u, (ushort)0),
    };

    /// <summary>Double-click variant (client or non-client) of a button-down message; 0 if it has none.</summary>
    public static uint DoubleClickMessage(uint downMessage) => downMessage switch
    {
        WM_LBUTTONDOWN => WM_LBUTTONDBLCLK,
        WM_RBUTTONDOWN => WM_RBUTTONDBLCLK,
        WM_MBUTTONDOWN => WM_MBUTTONDBLCLK,
        WM_XBUTTONDOWN => WM_XBUTTONDBLCLK,
        WM_NCLBUTTONDOWN => WM_NCLBUTTONDBLCLK,
        WM_NCRBUTTONDOWN => WM_NCRBUTTONDBLCLK,
        WM_NCMBUTTONDOWN => WM_NCMBUTTONDBLCLK,
        WM_NCXBUTTONDOWN => WM_NCXBUTTONDBLCLK,
        _ => 0,
    };

    /// <summary>Non-client counterpart of a client mouse message (move/down/up); 0 if none.</summary>
    public static uint NonClientMessage(uint clientMessage) => clientMessage switch
    {
        WM_MOUSEMOVE => WM_NCMOUSEMOVE,
        WM_LBUTTONDOWN => WM_NCLBUTTONDOWN,
        WM_LBUTTONUP => WM_NCLBUTTONUP,
        WM_RBUTTONDOWN => WM_NCRBUTTONDOWN,
        WM_RBUTTONUP => WM_NCRBUTTONUP,
        WM_MBUTTONDOWN => WM_NCMBUTTONDOWN,
        WM_MBUTTONUP => WM_NCMBUTTONUP,
        WM_XBUTTONDOWN => WM_NCXBUTTONDOWN,
        WM_XBUTTONUP => WM_NCXBUTTONUP,
        _ => 0,
    };

    /// <summary>A WM_NCHITTEST result that should be delivered as non-client input (not client, nowhere, transparent or error).</summary>
    public static bool IsNonClientHit(int hit) => hit is not (HTCLIENT or HTNOWHERE or HTTRANSPARENT or HTERROR);

    /// <summary>
    /// WM_NCLBUTTONDOWN on the caption (2) or a sizing border (10-17) makes DefWindowProc enter its modal move/size
    /// loop, which follows the Host's REAL mouse — exactly what message mode must avoid — so it is not posted.
    /// </summary>
    public static bool StartsModalMoveSize(uint ncMsg, int hit) => ncMsg == WM_NCLBUTTONDOWN && hit is 2 or (>= 10 and <= 17);

    /// <summary>
    /// Whether a press is the second click of a double-click, like Windows decides: same button and window as the
    /// previous press, within <paramref name="doubleClickTimeMs"/>, and inside a <paramref name="cx"/> x <paramref name="cy"/>
    /// rectangle centred on the previous press.
    /// </summary>
    public static bool IsDoubleClick(ClickRecord? previous, ClickRecord current, long doubleClickTimeMs, int cx, int cy)
    {
        if (previous is not { } prev)
            return false;
        if (prev.Button != current.Button || prev.Window != current.Window)
            return false;
        long elapsed = current.TimeMs - prev.TimeMs;
        if (elapsed < 0 || elapsed > doubleClickTimeMs)
            return false;
        return Math.Abs(current.X - prev.X) <= cx / 2 && Math.Abs(current.Y - prev.Y) <= cy / 2;
    }

    /// <summary>
    /// Ctrl/Alt chords on letter or digit keys are not posted as WM_KEYDOWN/UP in message mode: posted messages do not
    /// change GetKeyState, so the target would type the bare letter instead of running the shortcut.
    /// </summary>
    public static bool IsSuppressedChordKey(ushort virtualKey, bool ctrlHeld, bool altHeld)
        => (ctrlHeld || altHeld) && virtualKey is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A);

    /// <summary>Control character (0x01..0x1A) posted as WM_CHAR for Ctrl+letter without Alt; null otherwise.</summary>
    public static char? ControlChar(ushort virtualKey, bool ctrlHeld, bool altHeld)
        => ctrlHeld && !altHeld && virtualKey is >= 0x41 and <= 0x5A ? (char)(virtualKey - 0x40) : null;

    /// <summary>
    /// Keystroke lParam: bits 0-15 repeat count (1), 16-23 scan code, 24 extended key, 29 context code (Alt held, sys keys only),
    /// 30 previous key state (1 if it was already down; always 1 for key-up), 31 transition state (1 for key-up).
    /// </summary>
    public static nint KeyLParam(KeyMapping key, bool down, bool wasDown, bool contextAlt)
    {
        uint value = 1u | ((uint)(key.ScanCode & 0xFF) << 16);
        if (key.IsExtended)
            value |= 1u << 24;
        if (contextAlt)
            value |= 1u << 29;
        if (!down || wasDown)
            value |= 1u << 30;
        if (!down)
            value |= 1u << 31;
        return (nint)(int)value;
    }

    /// <summary>WM_SYSKEYDOWN/UP while Alt is held without Ctrl (as Windows does), otherwise WM_KEYDOWN/UP.</summary>
    public static uint KeyMessage(bool down, bool altHeld, bool ctrlHeld)
    {
        bool sys = altHeld && !ctrlHeld;
        return down ? (sys ? WM_SYSKEYDOWN : WM_KEYDOWN) : (sys ? WM_SYSKEYUP : WM_KEYUP);
    }
}
