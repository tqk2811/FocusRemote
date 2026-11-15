using System.Runtime.InteropServices;
using FocusRemote.Host.Core.Input.Interfaces;
using FocusRemote.Host.Core.Input.Models;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Core.Native;
using FocusRemote.Protocol.Enums;
using FocusRemote.Protocol.Messages.Models.Input;
using static FocusRemote.Host.Core.Native.NativeMethods;

namespace FocusRemote.Host.Core.Input;

/// <summary>
/// Replays viewer input with SendInput (protocol §5.3). Tracks held keys/buttons so <see cref="ReleaseAll"/>
/// can undo them. Not thread-safe: call from the session's receive thread only.
/// </summary>
public sealed class InputInjector : IInputInjector
{
    private static readonly int _inputSize = Marshal.SizeOf<INPUT>();

    private readonly IAppLog _log;
    private readonly HashSet<string> _heldKeys = new(StringComparer.Ordinal);
    private readonly HashSet<MouseButton> _heldButtons = [];
    private int _lastAbsX;
    private int _lastAbsY;

    public InputInjector(IAppLog log)
    {
        _log = log;
    }

    /// <param name="target">Current bounds (physical pixels) of the streamed window/monitor.</param>
    /// <param name="foregroundWindow">Window to bring to the foreground before clicks/keys; IntPtr.Zero for a monitor.</param>
    public void Handle(InputMessage message, PixelRect target, IntPtr foregroundWindow)
    {
        switch (message)
        {
            case MouseMoveInput m:
                MoveTo(m.X, m.Y, target);
                break;
            case MouseButtonInput m:
                if (!Enum.IsDefined(m.Button))
                    return;
                if (m.Down)
                    WindowActivator.EnsureForeground(foregroundWindow);
                MoveTo(m.X, m.Y, target);
                SendButton(m.Button, m.Down);
                break;
            case MouseWheelInput m:
                MoveTo(m.X, m.Y, target);
                if (m.DeltaY != 0)
                    SendMouse(MOUSEEVENTF_WHEEL, m.DeltaY);
                if (m.DeltaX != 0)
                    SendMouse(MOUSEEVENTF_HWHEEL, m.DeltaX);
                break;
            case KeyInput m:
                if (!KeyCodeMap.TryGet(m.Code, out KeyMapping key))
                    return;
                if (m.Down)
                    WindowActivator.EnsureForeground(foregroundWindow);
                SendKey(key, m.Down);
                if (m.Down)
                    _heldKeys.Add(m.Code);
                else
                    _heldKeys.Remove(m.Code);
                break;
            case TextInput m:
                WindowActivator.EnsureForeground(foregroundWindow);
                SendText(m.Text);
                break;
            case ReleaseAllInput:
                ReleaseAll();
                break;
        }
    }

    /// <summary>Releases every key and mouse button pressed through this injector.</summary>
    public void ReleaseAll()
    {
        foreach (string code in _heldKeys)
        {
            if (KeyCodeMap.TryGet(code, out KeyMapping key))
                SendKey(key, down: false);
        }
        _heldKeys.Clear();
        foreach (MouseButton button in _heldButtons.ToArray())
            SendButton(button, down: false);
        _heldButtons.Clear();
    }

    private void MoveTo(ushort x, ushort y, PixelRect target)
    {
        PixelRect virtualScreen = new(
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN));
        (_lastAbsX, _lastAbsY) = CoordinateMapper.ToVirtualDesktopAbsolute(x, y, target, virtualScreen);
        INPUT input = MouseInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0);
        input.u.mi.dx = _lastAbsX;
        input.u.mi.dy = _lastAbsY;
        Send([input]);
    }

    private void SendButton(MouseButton button, bool down)
    {
        (uint flags, int data) = button switch
        {
            MouseButton.Left => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0),
            MouseButton.Right => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0),
            MouseButton.Middle => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0),
            MouseButton.X1 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1),
            MouseButton.X2 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2),
            _ => (0u, 0),
        };
        if (flags == 0)
            return;
        SendMouse(flags, data);
        if (down)
            _heldButtons.Add(button);
        else
            _heldButtons.Remove(button);
    }

    private void SendMouse(uint flags, int data) => Send([MouseInput(flags, data)]);

    private static INPUT MouseInput(uint flags, int data) => new()
    {
        type = INPUT_MOUSE,
        u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } },
    };

    private void SendKey(KeyMapping key, bool down)
    {
        uint flags = (key.IsExtended ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP);
        Send([KeyInput(key.VirtualKey, key.ScanCode, flags)]);
    }

    private void SendText(string text)
    {
        List<INPUT> inputs = new(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\n')
            {
                inputs.Add(KeyInput(0x0D, 0x1C, 0));
                inputs.Add(KeyInput(0x0D, 0x1C, KEYEVENTF_KEYUP));
                continue;
            }
            if (c == '\r')
                continue;
            inputs.Add(KeyInput(0, c, KEYEVENTF_UNICODE));
            inputs.Add(KeyInput(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        if (inputs.Count > 0)
            Send(inputs.ToArray());
    }

    private static INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    private void Send(INPUT[] inputs)
    {
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, _inputSize);
        if (sent != inputs.Length)
            _log.Warn($"SendInput sent {sent}/{inputs.Length} (error {Marshal.GetLastWin32Error()}); target may run elevated.");
    }
}
