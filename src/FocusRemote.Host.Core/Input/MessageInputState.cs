using FocusRemote.Protocol.Enums;

namespace FocusRemote.Host.Core.Input;

/// <summary>
/// Held keys/buttons of the message-mode injector (no Win32 calls, unit-testable). Posted messages do not
/// change the system key state, so MK_* flags and Alt/Ctrl detection come from this tracker instead.
/// </summary>
internal sealed class MessageInputState
{
    private readonly HashSet<string> _heldKeys = new(StringComparer.Ordinal);
    private readonly HashSet<MouseButton> _heldButtons = [];

    public IReadOnlyCollection<string> HeldKeys => _heldKeys;

    public IReadOnlyCollection<MouseButton> HeldButtons => _heldButtons;

    public bool ShiftHeld => _heldKeys.Contains("ShiftLeft") || _heldKeys.Contains("ShiftRight");

    public bool CtrlHeld => _heldKeys.Contains("ControlLeft") || _heldKeys.Contains("ControlRight");

    public bool AltHeld => _heldKeys.Contains("AltLeft") || _heldKeys.Contains("AltRight");

    public bool AnyButtonHeld => _heldButtons.Count > 0;

    public bool IsKeyHeld(string code) => _heldKeys.Contains(code);

    /// <summary>Records a key press/release; returns whether the key was already down before.</summary>
    public bool SetKey(string code, bool down)
    {
        bool wasDown = _heldKeys.Contains(code);
        if (down)
            _heldKeys.Add(code);
        else
            _heldKeys.Remove(code);
        return wasDown;
    }

    public void SetButton(MouseButton button, bool down)
    {
        if (down)
            _heldButtons.Add(button);
        else
            _heldButtons.Remove(button);
    }

    /// <summary>MK_* wParam flags for the held buttons and Shift/Ctrl.</summary>
    public int MouseKeyFlags
    {
        get
        {
            int flags = 0;
            foreach (MouseButton button in _heldButtons)
                flags |= WindowMessageBuilder.ButtonFlag(button);
            if (ShiftHeld)
                flags |= WindowMessageBuilder.MK_SHIFT;
            if (CtrlHeld)
                flags |= WindowMessageBuilder.MK_CONTROL;
            return flags;
        }
    }

    public void Clear()
    {
        _heldKeys.Clear();
        _heldButtons.Clear();
    }
}
