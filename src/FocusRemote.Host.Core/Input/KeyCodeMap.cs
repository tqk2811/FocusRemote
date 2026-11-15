using FocusRemote.Host.Core.Input.Models;

namespace FocusRemote.Host.Core.Input;

/// <summary>
/// Browser <c>KeyboardEvent.code</c> (physical key, US layout names) → Windows virtual key + scan code.
/// Unknown codes return false and are ignored by the host.
/// </summary>
public static class KeyCodeMap
{
    private static readonly IReadOnlyDictionary<string, KeyMapping> _map = Build();

    public static bool TryGet(string? code, out KeyMapping mapping)
    {
        mapping = default;
        return code is not null && _map.TryGetValue(code, out mapping);
    }

    public static IReadOnlyCollection<string> KnownCodes => (IReadOnlyCollection<string>)_map.Keys;

    private static Dictionary<string, KeyMapping> Build()
    {
        var map = new Dictionary<string, KeyMapping>(StringComparer.Ordinal);
        void Add(string code, int vk, int scan, bool extended = false) => map.Add(code, new KeyMapping((ushort)vk, (ushort)scan, extended));

        // Letters (scan codes of the QWERTY rows).
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        int[] letterScans = [0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C];
        for (int i = 0; i < letters.Length; i++)
            Add("Key" + letters[i], 'A' + i, letterScans[i]);

        // Digit row: Digit1..Digit9 = scan 0x02..0x0A, Digit0 = 0x0B.
        for (int d = 1; d <= 9; d++)
            Add("Digit" + d, '0' + d, 0x01 + d);
        Add("Digit0", '0', 0x0B);

        // Function keys.
        for (int f = 1; f <= 10; f++)
            Add("F" + f, 0x6F + f, 0x3A + f);
        Add("F11", 0x7A, 0x57);
        Add("F12", 0x7B, 0x58);
        int[] f13to23 = [0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E];
        for (int i = 0; i < f13to23.Length; i++)
            Add("F" + (13 + i), 0x7C + i, f13to23[i]);
        Add("F24", 0x87, 0x76);

        // Main block.
        Add("Escape", 0x1B, 0x01);
        Add("Minus", 0xBD, 0x0C);
        Add("Equal", 0xBB, 0x0D);
        Add("Backspace", 0x08, 0x0E);
        Add("Tab", 0x09, 0x0F);
        Add("BracketLeft", 0xDB, 0x1A);
        Add("BracketRight", 0xDD, 0x1B);
        Add("Enter", 0x0D, 0x1C);
        Add("ControlLeft", 0xA2, 0x1D);
        Add("Semicolon", 0xBA, 0x27);
        Add("Quote", 0xDE, 0x28);
        Add("Backquote", 0xC0, 0x29);
        Add("ShiftLeft", 0xA0, 0x2A);
        Add("Backslash", 0xDC, 0x2B);
        Add("Comma", 0xBC, 0x33);
        Add("Period", 0xBE, 0x34);
        Add("Slash", 0xBF, 0x35);
        Add("ShiftRight", 0xA1, 0x36);
        Add("AltLeft", 0xA4, 0x38);
        Add("Space", 0x20, 0x39);
        Add("CapsLock", 0x14, 0x3A);
        Add("ScrollLock", 0x91, 0x46);
        Add("Pause", 0x13, 0x45);
        Add("IntlBackslash", 0xE2, 0x56);
        Add("IntlRo", 0xC1, 0x73);

        // Numpad.
        Add("NumLock", 0x90, 0x45, extended: true);
        Add("NumpadMultiply", 0x6A, 0x37);
        Add("NumpadSubtract", 0x6D, 0x4A);
        Add("NumpadAdd", 0x6B, 0x4E);
        Add("NumpadDecimal", 0x6E, 0x53);
        Add("NumpadDivide", 0x6F, 0x35, extended: true);
        Add("NumpadEnter", 0x0D, 0x1C, extended: true);
        int[] numpadScans = [0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49];
        for (int n = 0; n <= 9; n++)
            Add("Numpad" + n, 0x60 + n, numpadScans[n]);

        // Extended (E0-prefixed) keys.
        Add("ControlRight", 0xA3, 0x1D, extended: true);
        Add("AltRight", 0xA5, 0x38, extended: true);
        Add("PrintScreen", 0x2C, 0x37, extended: true);
        Add("Home", 0x24, 0x47, extended: true);
        Add("ArrowUp", 0x26, 0x48, extended: true);
        Add("PageUp", 0x21, 0x49, extended: true);
        Add("ArrowLeft", 0x25, 0x4B, extended: true);
        Add("ArrowRight", 0x27, 0x4D, extended: true);
        Add("End", 0x23, 0x4F, extended: true);
        Add("ArrowDown", 0x28, 0x50, extended: true);
        Add("PageDown", 0x22, 0x51, extended: true);
        Add("Insert", 0x2D, 0x52, extended: true);
        Add("Delete", 0x2E, 0x53, extended: true);
        Add("MetaLeft", 0x5B, 0x5B, extended: true);
        Add("MetaRight", 0x5C, 0x5C, extended: true);
        Add("OSLeft", 0x5B, 0x5B, extended: true);
        Add("OSRight", 0x5C, 0x5C, extended: true);
        Add("ContextMenu", 0x5D, 0x5D, extended: true);

        // Media keys.
        Add("AudioVolumeMute", 0xAD, 0x20, extended: true);
        Add("AudioVolumeDown", 0xAE, 0x2E, extended: true);
        Add("AudioVolumeUp", 0xAF, 0x30, extended: true);
        Add("MediaTrackNext", 0xB0, 0x19, extended: true);
        Add("MediaTrackPrevious", 0xB1, 0x10, extended: true);
        Add("MediaStop", 0xB2, 0x24, extended: true);
        Add("MediaPlayPause", 0xB3, 0x22, extended: true);
        return map;
    }
}
