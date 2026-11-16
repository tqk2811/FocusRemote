using FocusRemote.Host.Core.Terminal.Models;

namespace FocusRemote.Host.Core.Terminal.Interfaces;

/// <summary>Shells the viewer may start (chosen by id only).</summary>
public interface IShellCatalog
{
    IReadOnlyList<ShellDefinition> GetShells();

    ShellDefinition? Find(string id);
}
