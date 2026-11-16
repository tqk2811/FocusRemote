using System.Runtime.InteropServices;
using FocusRemote.Host.Core.Input.Models;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Models;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Messages.Models.Input;
using static FocusRemote.Host.Core.Input.WindowMessageBuilder;
using static FocusRemote.Host.Core.Native.NativeMethods;

namespace FocusRemote.Host.Core.Input;

/// <summary>
/// "Message" input mode: posts mouse/keyboard window messages to the streamed window instead of SendInput,
/// so the host's real cursor/keyboard focus are untouched and the window is never activated.
/// Works only with apps that read input from posted messages (not raw input / DirectInput / GetAsyncKeyState).
/// Not thread-safe: called from the session's receive thread only.
/// </summary>
internal sealed class MessageInputInjector
{
    private const uint CWP_SKIPINVISIBLE = 0x0001;
    private const uint CWP_SKIPDISABLED = 0x0002;
    private const uint CWP_SKIPTRANSPARENT = 0x0004;
    private const int MaxChildDepth = 32;
    private const uint HitTestTimeoutMs = 100;

    private readonly IAppLog _log;
    private readonly MessageInputState _state = new();
    private IntPtr _window;
    private IntPtr _captureTarget;
    private int _captureHit = HTCLIENT;
    private IntPtr _lastMouseTarget;
    private int _lastHit = HTCLIENT;
    private int _lastScreenX;
    private int _lastScreenY;
    private ClickRecord? _lastClick;
    private bool _postFailureLogged;

    public MessageInputInjector(IAppLog log)
    {
        _log = log;
    }

    /// <param name="target">Current bounds (physical pixels) of the streamed window.</param>
    /// <param name="window">Top-level window being streamed.</param>
    public void Handle(InputMessage message, PixelRect target, IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window))
            return;
        if (window != _window)
        {
            ReleaseAll();
            _window = window;
        }
        switch (message)
        {
            case MouseMoveInput m:
                PostMouse(WM_MOUSEMOVE, 0, null, m.X, m.Y, target);
                break;
            case MouseButtonInput m:
            {
                if (!Enum.IsDefined(m.Button))
                    return;
                (uint msg, ushort xButton) = ButtonMessage(m.Button, m.Down);
                // MK_* flags in a button message describe the state after the change.
                _state.SetButton(m.Button, m.Down);
                PostMouse(msg, xButton, m.Down ? m.Button : null, m.X, m.Y, target);
                if (!_state.AnyButtonHeld)
                {
                    _captureTarget = IntPtr.Zero;
                    _captureHit = HTCLIENT;
                }
                break;
            }
            case MouseWheelInput m:
                PostWheel(m, target);
                break;
            case KeyInput m:
                if (KeyCodeMap.TryGet(m.Code, out KeyMapping key))
                    PostKey(m.Code, key, m.Down);
                break;
            case TextInput m:
                PostText(m.Text);
                break;
            case ReleaseAllInput:
                ReleaseAll();
                break;
        }
    }

    /// <summary>Posts up messages for every held button and key, then forgets them.</summary>
    public void ReleaseAll()
    {
        if (_window != IntPtr.Zero && IsWindow(_window))
        {
            bool captured = _captureTarget != IntPtr.Zero;
            IntPtr mouseTarget = captured ? _captureTarget : _lastMouseTarget;
            int hit = captured ? _captureHit : _lastHit;
            foreach (MouseButton button in _state.HeldButtons.ToArray())
            {
                _state.SetButton(button, false);
                (uint msg, ushort xButton) = ButtonMessage(button, false);
                if (mouseTarget != IntPtr.Zero)
                    PostMouseTo(mouseTarget, hit, msg, xButton, _lastScreenX, _lastScreenY);
            }
            foreach (string code in _state.HeldKeys.ToArray())
            {
                if (KeyCodeMap.TryGet(code, out KeyMapping key))
                    PostKey(code, key, down: false);
            }
        }
        _state.Clear();
        _captureTarget = IntPtr.Zero;
        _captureHit = HTCLIENT;
        _lastMouseTarget = IntPtr.Zero;
        _lastHit = HTCLIENT;
        _lastClick = null;
    }

    /// <param name="pressed">Button just pressed (double-click detection), null for moves and releases.</param>
    private void PostMouse(uint msg, ushort xButton, MouseButton? pressed, ushort x, ushort y, PixelRect target)
    {
        (_lastScreenX, _lastScreenY) = CoordinateMapper.ToScreen(x, y, target);
        IntPtr hwnd;
        int hit;
        // While a button is held, keep sending to the window/area that got the press (like SetCapture would).
        if (_captureTarget != IntPtr.Zero && IsWindow(_captureTarget))
        {
            hwnd = _captureTarget;
            hit = _captureHit;
        }
        else
        {
            (hwnd, hit) = ResolveTarget(_lastScreenX, _lastScreenY);
            if (_state.AnyButtonHeld)
            {
                _captureTarget = hwnd;
                _captureHit = hit;
            }
        }
        _lastMouseTarget = hwnd;
        _lastHit = hit;
        if (IsNonClientHit(hit))
            msg = NonClientMessage(msg); // before the double-click mapping, which knows both forms

        if (pressed is { } button)
        {
            ClickRecord click = new(button, hwnd, Environment.TickCount64, _lastScreenX, _lastScreenY);
            bool dblClkWanted = IsNonClientHit(hit) || HasDoubleClickStyle(hwnd);
            if (dblClkWanted && IsDoubleClick(_lastClick, click, GetDoubleClickTime(),
                    GetSystemMetrics(SM_CXDOUBLECLK), GetSystemMetrics(SM_CYDOUBLECLK)))
            {
                msg = DoubleClickMessage(msg);
                _lastClick = null; // a third press starts a new click pair
            }
            else
            {
                _lastClick = click;
            }
        }
        PostMouseTo(hwnd, hit, msg, xButton, _lastScreenX, _lastScreenY);
    }

    /// <summary>Client message (client coordinates, MK_* flags) or its non-client form (hit code, screen coordinates).</summary>
    private void PostMouseTo(IntPtr hwnd, int hit, uint msg, ushort xButton, int screenX, int screenY)
    {
        if (IsNonClientHit(hit))
        {
            // Already-non-client ids (0x00A0..0x00AD) pass through; client ids are mapped.
            uint ncMsg = msg >= WM_MOUSEMOVE ? NonClientMessage(msg) : msg;
            if (ncMsg != 0 && !StartsModalMoveSize(ncMsg, hit))
                Post(hwnd, ncMsg, WParam(hit, xButton), PointLParam(screenX, screenY));
            return;
        }
        POINT pt = new() { X = screenX, Y = screenY };
        ScreenToClient(hwnd, ref pt);
        Post(hwnd, msg, WParam(_state.MouseKeyFlags, xButton), PointLParam(pt.X, pt.Y));
    }

    /// <summary>Deepest child under the point; if the point lies outside that window's client area, its WM_NCHITTEST result.</summary>
    private (IntPtr Window, int Hit) ResolveTarget(int screenX, int screenY)
    {
        IntPtr hwnd = ChildFromScreenPoint(_window, screenX, screenY);
        if (IsInsideClient(hwnd, screenX, screenY))
            return (hwnd, HTCLIENT);
        int hit = HitTest(hwnd, screenX, screenY);
        return (hwnd, IsNonClientHit(hit) ? hit : HTCLIENT);
    }

    private static bool IsInsideClient(IntPtr hwnd, int screenX, int screenY)
    {
        POINT pt = new() { X = screenX, Y = screenY };
        if (!ScreenToClient(hwnd, ref pt) || !GetClientRect(hwnd, out RECT rc))
            return true;
        return pt.X >= rc.Left && pt.X < rc.Right && pt.Y >= rc.Top && pt.Y < rc.Bottom;
    }

    /// <summary>WM_NCHITTEST with a short timeout so a hung app cannot block the session's receive loop.</summary>
    private static int HitTest(IntPtr hwnd, int screenX, int screenY)
    {
        IntPtr ok = SendMessageTimeout(hwnd, WM_NCHITTEST, 0, PointLParam(screenX, screenY), SMTO_ABORTIFHUNG, HitTestTimeoutMs, out nint result);
        return ok == IntPtr.Zero ? HTCLIENT : (int)result;
    }

    private static bool HasDoubleClickStyle(IntPtr hwnd) => (GetClassLongPtr(hwnd, GCL_STYLE) & CS_DBLCLKS) != 0;

    private void PostWheel(MouseWheelInput m, PixelRect target)
    {
        (_lastScreenX, _lastScreenY) = CoordinateMapper.ToScreen(m.X, m.Y, target);
        IntPtr hwnd = ChildFromScreenPoint(_window, _lastScreenX, _lastScreenY);
        _lastMouseTarget = hwnd;
        _lastHit = HTCLIENT;
        // Wheel messages carry screen coordinates (not client) in lParam.
        nint lParam = PointLParam(_lastScreenX, _lastScreenY);
        if (m.DeltaY != 0)
            Post(hwnd, WM_MOUSEWHEEL, WParam(_state.MouseKeyFlags, m.DeltaY), lParam);
        if (m.DeltaX != 0)
            Post(hwnd, WM_MOUSEHWHEEL, WParam(_state.MouseKeyFlags, m.DeltaX), lParam);
    }

    private void PostKey(string code, KeyMapping key, bool down)
    {
        bool wasDown = _state.IsKeyHeld(code);
        if (down)
            _state.SetKey(code, true);
        // Alt counts as held for its own press and release, so it gets WM_SYSKEYDOWN/UP like a real keystroke.
        bool alt = _state.AltHeld;
        bool ctrl = _state.CtrlHeld;
        if (!down)
            _state.SetKey(code, false);
        if (IsSuppressedChordKey(key.VirtualKey, ctrl, alt))
        {
            // Shortcuts cannot work without the real key state; post only the control char (Ctrl+A → 0x01) on press.
            if (down && ControlChar(key.VirtualKey, ctrl, alt) is { } controlChar)
                Post(FocusTarget(), WM_CHAR, controlChar, KeyLParam(key, true, wasDown, false));
            return;
        }
        uint msg = KeyMessage(down, alt, ctrl);
        bool sys = msg is WM_SYSKEYDOWN or WM_SYSKEYUP;
        Post(FocusTarget(), msg, key.VirtualKey, KeyLParam(key, down, wasDown, contextAlt: sys));
    }

    private void PostText(string text)
    {
        IntPtr hwnd = FocusTarget();
        KeyMapping enter = new(0x0D, 0x1C, false);
        foreach (char c in text)
        {
            if (c == '\r')
                continue;
            if (c == '\n')
            {
                Post(hwnd, WM_KEYDOWN, enter.VirtualKey, KeyLParam(enter, true, false, false));
                Post(hwnd, WM_CHAR, '\r', KeyLParam(enter, true, false, false));
                Post(hwnd, WM_KEYUP, enter.VirtualKey, KeyLParam(enter, false, true, false));
                continue;
            }
            Post(hwnd, WM_CHAR, c, 1);
        }
    }

    /// <summary>Focused control of the window's GUI thread if it belongs to that window, else the window itself.</summary>
    private IntPtr FocusTarget()
    {
        uint threadId = GetWindowThreadProcessId(_window, out _);
        if (threadId != 0)
        {
            GUITHREADINFO info = new() { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != IntPtr.Zero
                && GetAncestor(info.hwndFocus, GA_ROOT) == _window)
                return info.hwndFocus;
        }
        return _window;
    }

    /// <summary>
    /// Deepest child of <paramref name="root"/> under a screen point (root itself if none). Uses RealChildWindowFromPoint
    /// (skips group boxes / HTTRANSPARENT children); falls back to ChildWindowFromPointEx when that returns a hidden or
    /// disabled window.
    /// </summary>
    private static IntPtr ChildFromScreenPoint(IntPtr root, int screenX, int screenY)
    {
        IntPtr current = root;
        for (int depth = 0; depth < MaxChildDepth; depth++)
        {
            POINT pt = new() { X = screenX, Y = screenY };
            if (!ScreenToClient(current, ref pt))
                break;
            IntPtr child = RealChildWindowFromPoint(current, pt);
            if (child != IntPtr.Zero && child != current && (!IsWindowVisible(child) || !IsWindowEnabled(child)))
                child = ChildWindowFromPointEx(current, pt, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);
            if (child == IntPtr.Zero || child == current)
                break;
            current = child;
        }
        return current;
    }

    private void Post(IntPtr hwnd, uint msg, nint wParam, nint lParam)
    {
        if (PostMessage(hwnd, msg, wParam, lParam) || _postFailureLogged)
            return;
        _postFailureLogged = true;
        _log.Warn($"PostMessage 0x{msg:X4} failed (error {Marshal.GetLastWin32Error()}); target may run elevated (UIPI).");
    }
}
