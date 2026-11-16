using FocusRemote.Host.Core.Terminal.Interfaces;
using FocusRemote.Host.Core.Terminal.Models;

namespace FocusRemote.Host.Core.Terminal;

/// <summary>Known shells that exist on this machine (cmd, Windows PowerShell, PowerShell 7, Git Bash, WSL), detected once.</summary>
public sealed class ShellCatalog : IShellCatalog
{
    private readonly IReadOnlyList<ShellDefinition> _shells;

    public ShellCatalog()
    {
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var shells = new List<ShellDefinition>();
        Add(shells, "cmd", "Command Prompt", Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(system32, "cmd.exe"), null);
        Add(shells, "powershell", "Windows PowerShell", Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoLogo");
        string? pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(pwsh))
            pwsh = FindOnPath("pwsh.exe");
        if (pwsh is not null)
            Add(shells, "pwsh", "PowerShell 7", pwsh, "-NoLogo");
        Add(shells, "gitbash", "Git Bash", Path.Combine(programFiles, "Git", "bin", "bash.exe"), "--login -i");
        Add(shells, "wsl", "WSL", Path.Combine(system32, "wsl.exe"), null);
        _shells = shells;
    }

    public IReadOnlyList<ShellDefinition> GetShells() => _shells;

    public ShellDefinition? Find(string id) => _shells.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    private static void Add(List<ShellDefinition> shells, string id, string name, string path, string? arguments)
    {
        if (!File.Exists(path))
            return;
        string commandLine = "\"" + path + "\"" + (arguments is null ? string.Empty : " " + arguments);
        shells.Add(new ShellDefinition { Id = id, Name = name, CommandLine = commandLine });
    }

    private static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
            return null;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }
}
