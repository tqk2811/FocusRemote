using static FocusRemote.Host.Core.Native.NativeMethods;

namespace FocusRemote.Host.Core.Input;

/// <summary>Brings a window to the foreground despite the foreground lock (AttachThreadInput + Alt key trick).</summary>
internal static class WindowActivator
{
    public static void EnsureForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return;
        IntPtr foreground = GetForegroundWindow();
        if (foreground == hwnd)
            return;
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);

        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        bool attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            // A synthetic Alt press makes this process the "last input" owner so SetForegroundWindow is allowed.
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}
