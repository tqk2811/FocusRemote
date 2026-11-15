using FocusRemote.Host.Core.Models;

namespace FocusRemote.Host.Core.Input;

/// <summary>Maps protocol-normalized coordinates (u16, 0 = left/top, 65535 = right/bottom) to screen/SendInput coordinates.</summary>
public static class CoordinateMapper
{
    public const int NormalizedMax = 65535;

    /// <summary>Physical pixel inside <paramref name="target"/> (last pixel for 65535).</summary>
    public static (int X, int Y) ToScreen(ushort x, ushort y, PixelRect target)
    {
        int px = target.Left + (int)Math.Round(x / (double)NormalizedMax * Math.Max(0, target.Width - 1));
        int py = target.Top + (int)Math.Round(y / (double)NormalizedMax * Math.Max(0, target.Height - 1));
        return (px, py);
    }

    /// <summary>
    /// Converts a physical screen pixel to the 0..65535 absolute range of SendInput with
    /// MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, relative to <paramref name="virtualScreen"/>.
    /// Points outside the virtual desktop are clamped.
    /// </summary>
    public static (int X, int Y) ToVirtualDesktopAbsolute(int screenX, int screenY, PixelRect virtualScreen)
    {
        int ax = Scale(screenX - virtualScreen.Left, virtualScreen.Width);
        int ay = Scale(screenY - virtualScreen.Top, virtualScreen.Height);
        return (ax, ay);
    }

    public static (int X, int Y) ToVirtualDesktopAbsolute(ushort x, ushort y, PixelRect target, PixelRect virtualScreen)
    {
        (int sx, int sy) = ToScreen(x, y, target);
        return ToVirtualDesktopAbsolute(sx, sy, virtualScreen);
    }

    private static int Scale(int offset, int extent)
    {
        if (extent <= 1)
            return 0;
        int clamped = Math.Clamp(offset, 0, extent - 1);
        return (int)Math.Round(clamped * (double)NormalizedMax / (extent - 1));
    }
}
