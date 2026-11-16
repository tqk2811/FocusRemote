using FocusRemote.Host.Core.Input.Enums;
using FocusRemote.Host.Core.Input.Interfaces;
using FocusRemote.Host.Core.Logging.Interfaces;
using FocusRemote.Host.Core.Models;
using FocusRemote.Protocol.Messages.Models.Input;

namespace FocusRemote.Host.Core.Input;

/// <summary>
/// Dry-run injector: logs every input message (with the mapped desktop point) instead of calling SendInput.
/// Used by the E2E harness so tests never touch the real desktop.
/// </summary>
public sealed class LoggingInputInjector : IInputInjector
{
    public const string LogPrefix = "INPUT ";

    private readonly IAppLog _log;

    public LoggingInputInjector(IAppLog log)
    {
        _log = log;
    }

    public void Handle(InputMessage message, PixelRect target, IntPtr foregroundWindow)
        => _log.Info(LogPrefix + Describe(message, target));

    public void ReleaseAll() => _log.Info(LogPrefix + "ReleaseAll");

    public void SetMode(InputMode mode) => _log.Info(LogPrefix + "Mode " + mode);

    /// <summary>Human-readable form, e.g. <c>MouseMove x=100 y=50</c> (desktop pixels).</summary>
    public static string Describe(InputMessage message, PixelRect target) => message switch
    {
        MouseMoveInput m => $"MouseMove {Point(m.X, m.Y, target)}",
        MouseButtonInput m => $"MouseButton {m.Button} down={m.Down} {Point(m.X, m.Y, target)}",
        MouseWheelInput m => $"MouseWheel dx={m.DeltaX} dy={m.DeltaY} {Point(m.X, m.Y, target)}",
        KeyInput m => $"Key {m.Code} down={m.Down}",
        TextInput m => $"Text '{m.Text}'",
        ReleaseAllInput => "ReleaseAll",
        _ => message.GetType().Name,
    };

    private static string Point(ushort x, ushort y, PixelRect target)
    {
        (int px, int py) = CoordinateMapper.ToScreen(x, y, target);
        return $"x={px} y={py}";
    }
}
