using FocusRemote.Host.Core.Terminal.Models;

namespace FocusRemote.Host.Core.Terminal.Interfaces;

/// <summary>Starts shells; the default is <see cref="PseudoConsoleProcessFactory"/>, tests inject fakes.</summary>
public interface ITerminalProcessFactory
{
    /// <summary>Throws when the shell cannot be started.</summary>
    ITerminalProcess Start(ShellDefinition shell, int cols, int rows, ITerminalProcessListener listener);
}
