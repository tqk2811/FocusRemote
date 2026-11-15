using FocusRemote.Host.Core.Models;
using FocusRemote.Host.Core.Sources.Models;

namespace FocusRemote.Host.Core.Sources.Interfaces;

/// <summary>Enumerates capturable sources and resolves the live bounds of one (for input mapping).</summary>
public interface ISourceCatalog
{
    IReadOnlyList<CaptureSource> GetSources();

    /// <summary>Resolves an id from <see cref="GetSources"/> again; null if it no longer exists.</summary>
    CaptureSource? Find(string id);

    /// <summary>Current bounds of the source in physical pixels; null if gone.</summary>
    PixelRect? GetCurrentBounds(CaptureSource source);
}
