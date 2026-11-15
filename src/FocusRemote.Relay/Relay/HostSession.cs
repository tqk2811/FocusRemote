namespace FocusRemote.Relay.Relay;

/// <summary>A registered host and its (at most one) paired viewer.</summary>
internal sealed class HostSession
{
    private readonly object _lock = new();
    private PeerConnection? _viewer;
    private bool _closed;

    public HostSession(string id, PeerConnection host)
    {
        Id = id;
        Host = host;
    }

    public string Id { get; }

    public PeerConnection Host { get; }

    public PeerConnection? Viewer
    {
        get { lock (_lock) return _viewer; }
    }

    /// <summary>Attaches a viewer; false if one is already attached or the host is gone.</summary>
    public bool TryAttachViewer(PeerConnection viewer)
    {
        lock (_lock)
        {
            if (_closed || _viewer is not null)
                return false;
            _viewer = viewer;
            return true;
        }
    }

    /// <summary>Detaches the viewer if it is the current one; true if it was detached.</summary>
    public bool DetachViewer(PeerConnection viewer)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_viewer, viewer))
                return false;
            _viewer = null;
            return true;
        }
    }

    /// <summary>Marks the host as gone and returns the viewer to notify, if any.</summary>
    public PeerConnection? MarkClosed()
    {
        lock (_lock)
        {
            _closed = true;
            PeerConnection? viewer = _viewer;
            _viewer = null;
            return viewer;
        }
    }
}
