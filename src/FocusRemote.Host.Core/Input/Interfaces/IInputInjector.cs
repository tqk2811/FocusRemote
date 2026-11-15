using FocusRemote.Host.Core.Models;
using FocusRemote.Protocol.Messages.Models.Input;

namespace FocusRemote.Host.Core.Input.Interfaces;

/// <summary>Replays viewer input (protocol §5.3). One instance per viewer session; called from its receive thread only.</summary>
public interface IInputInjector
{
    /// <param name="target">Current bounds (physical pixels) of the streamed window/monitor.</param>
    /// <param name="foregroundWindow">Window to bring to the foreground before clicks/keys; IntPtr.Zero for a monitor.</param>
    void Handle(InputMessage message, PixelRect target, IntPtr foregroundWindow);

    /// <summary>Releases every key and mouse button pressed through this injector.</summary>
    void ReleaseAll();
}
