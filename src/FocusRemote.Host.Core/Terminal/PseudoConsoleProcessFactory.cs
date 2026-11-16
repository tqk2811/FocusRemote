using FocusRemote.Host.Core.Terminal.Interfaces;
using FocusRemote.Host.Core.Terminal.Models;

namespace FocusRemote.Host.Core.Terminal;

/// <summary>Starts shells in a ConPTY with the user profile as working directory.</summary>
internal sealed class PseudoConsoleProcessFactory : ITerminalProcessFactory
{
    private readonly string _workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public ITerminalProcess Start(ShellDefinition shell, int cols, int rows, ITerminalProcessListener listener)
        => new PseudoConsoleProcess(shell.CommandLine, _workingDirectory, cols, rows, listener);
}
