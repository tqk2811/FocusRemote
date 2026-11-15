namespace FocusRemote.Host.Core;

/// <summary>Per-user data locations under <c>%AppData%\FocusRemote</c>.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusRemote");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string LanCertificateFile => Path.Combine(DataDirectory, "lan-certificate.pfx.dpapi");
}
