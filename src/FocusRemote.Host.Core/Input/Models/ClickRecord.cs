using FocusRemote.Protocol.Enums;

namespace FocusRemote.Host.Core.Input.Models;

/// <summary>A button press remembered for double-click detection: button, target window, tick (ms) and screen point.</summary>
internal readonly record struct ClickRecord(MouseButton Button, IntPtr Window, long TimeMs, int X, int Y);
