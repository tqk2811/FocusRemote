namespace FocusRemote.Host.Core.Input.Models;

/// <summary>Windows key for a browser <c>KeyboardEvent.code</c>: virtual key, set-1 scan code, extended flag (E0 prefix).</summary>
public readonly record struct KeyMapping(ushort VirtualKey, ushort ScanCode, bool IsExtended);
