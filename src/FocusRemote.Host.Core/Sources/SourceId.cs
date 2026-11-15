using System.Globalization;
using FocusRemote.Host.Core.Sources.Enums;

namespace FocusRemote.Host.Core.Sources;

/// <summary>Source id format of protocol §5.1: window <c>w:&lt;hwnd decimal&gt;</c>, monitor <c>m:&lt;index&gt;</c>.</summary>
public static class SourceId
{
    public static string ForWindow(IntPtr hwnd) => "w:" + hwnd.ToInt64().ToString(CultureInfo.InvariantCulture);

    public static string ForMonitor(int index) => "m:" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses an id; value is the HWND (window) or the monitor index. False for anything malformed.</summary>
    public static bool TryParse(string? id, out SourceKind kind, out long value)
    {
        kind = default;
        value = 0;
        if (id is null || id.Length < 3 || id[1] != ':')
            return false;
        string number = id[2..];
        // Plain decimal digits only (no sign, no whitespace, no leading '+').
        foreach (char c in number)
        {
            if (c is < '0' or > '9')
                return false;
        }
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return false;
        switch (id[0])
        {
            case 'w':
                kind = SourceKind.Window;
                return value != 0;
            case 'm':
                kind = SourceKind.Monitor;
                return value <= int.MaxValue;
            default:
                return false;
        }
    }
}
