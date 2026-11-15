using System.Runtime.InteropServices;
using System.Text;
using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Core.Native;
using FocusRemote.Host.Core.Sources.Enums;
using FocusRemote.Host.Core.Sources.Interfaces;
using FocusRemote.Host.Core.Sources.Models;

namespace FocusRemote.Host.Core.Sources;

/// <summary>
/// Lists top-level windows (visible, not cloaked, titled, not tool windows, Alt-Tab style) and monitors
/// (EnumDisplayMonitors order = index of <c>m:&lt;index&gt;</c>). Requires a per-monitor DPI aware process
/// so all bounds are physical pixels.
/// </summary>
public sealed class SourceCatalog : ISourceCatalog
{
    public IReadOnlyList<CaptureSource> GetSources()
    {
        List<CaptureSource> result = [];
        result.AddRange(GetMonitors());
        result.AddRange(GetWindows());
        return result;
    }

    public CaptureSource? Find(string id)
    {
        if (!SourceId.TryParse(id, out SourceKind kind, out long value))
            return null;
        if (kind == SourceKind.Monitor)
        {
            IReadOnlyList<CaptureSource> monitors = GetMonitors();
            return value < monitors.Count ? monitors[(int)value] : null;
        }
        IntPtr hwnd = new(value);
        if (!NativeMethods.IsWindow(hwnd))
            return null;
        PixelRect? bounds = GetWindowBounds(hwnd);
        if (bounds is null)
            return null;
        return new CaptureSource
        {
            Id = id,
            Kind = SourceKind.Window,
            Title = GetTitle(hwnd),
            Handle = hwnd,
            Bounds = bounds.Value,
        };
    }

    public PixelRect? GetCurrentBounds(CaptureSource source)
    {
        if (source.Kind == SourceKind.Window)
            return NativeMethods.IsWindow(source.Handle) ? GetWindowBounds(source.Handle) : null;
        return GetMonitorBounds(source.Handle, out _);
    }

    private static List<CaptureSource> GetMonitors()
    {
        List<IntPtr> handles = [];
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            handles.Add(h);
            return true;
        }, IntPtr.Zero);

        List<CaptureSource> result = [];
        for (int i = 0; i < handles.Count; i++)
        {
            PixelRect? bounds = GetMonitorBounds(handles[i], out string device);
            if (bounds is null)
                continue;
            result.Add(new CaptureSource
            {
                Id = SourceId.ForMonitor(i),
                Kind = SourceKind.Monitor,
                Title = $"Màn hình {i + 1} ({device.TrimStart('\\', '.')}) {bounds.Value.Width}x{bounds.Value.Height}",
                Handle = handles[i],
                Bounds = bounds.Value,
            });
        }
        return result;
    }

    private static PixelRect? GetMonitorBounds(IntPtr hMonitor, out string device)
    {
        var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = string.Empty };
        device = string.Empty;
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
            return null;
        device = info.szDevice ?? string.Empty;
        NativeMethods.RECT r = info.rcMonitor;
        return PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static List<CaptureSource> GetWindows()
    {
        List<CaptureSource> result = [];
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (IsCapturableWindow(hwnd))
            {
                PixelRect? bounds = GetWindowBounds(hwnd);
                if (bounds is { IsEmpty: false })
                {
                    result.Add(new CaptureSource
                    {
                        Id = SourceId.ForWindow(hwnd),
                        Kind = SourceKind.Window,
                        Title = GetTitle(hwnd),
                        Handle = hwnd,
                        Bounds = bounds.Value,
                    });
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsCapturableWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
            return false;
        if (NativeMethods.GetWindowTextLength(hwnd) == 0)
            return false;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER) != hwnd)
            return false;
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        return true;
    }

    /// <summary>DWM extended frame bounds (what Windows.Graphics.Capture captures), falling back to GetWindowRect.</summary>
    internal static PixelRect? GetWindowBounds(IntPtr hwnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT r, Marshal.SizeOf<NativeMethods.RECT>()) != 0
            && !NativeMethods.GetWindowRect(hwnd, out r))
            return null;
        return PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;
        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }
}
