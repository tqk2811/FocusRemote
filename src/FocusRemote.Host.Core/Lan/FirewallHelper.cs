using System.Diagnostics;
using System.Security.Principal;

namespace FocusRemote.Host.Core.Lan;

/// <summary>Inbound Windows Firewall rule for the LAN port (needs admin; otherwise the UI shows the command).</summary>
public static class FirewallHelper
{
    public static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string RuleName(int port) => $"FocusRemote LAN {port}";

    /// <summary>The command a user can run in an elevated prompt.</summary>
    public static string AddRuleCommand(int port)
        => $"netsh advfirewall firewall add rule name=\"{RuleName(port)}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain";

    public static bool RuleExists(int port) => RunNetsh($"advfirewall firewall show rule name=\"{RuleName(port)}\"") == 0;

    /// <summary>Adds the rule if missing. Returns false when not elevated or netsh fails.</summary>
    public static bool TryAddRule(int port)
    {
        if (RuleExists(port))
            return true;
        if (!IsAdministrator())
            return false;
        return RunNetsh(AddRuleCommand(port)["netsh ".Length..]) == 0;
    }

    private static int RunNetsh(string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return -1;
            // Drain both pipes so netsh never blocks on a full buffer.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return -1;
            }
            Task.WaitAll(stdout, stderr);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
