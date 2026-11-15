using Microsoft.Win32;

namespace FocusRemote.Host.Core.Settings;

/// <summary>"Start with Windows" via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FocusRemote";

    public static void Apply(bool enabled, string executablePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, $"\"{executablePath}\" --minimized");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
