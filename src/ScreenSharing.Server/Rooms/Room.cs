namespace ScreenSharing.Server.Rooms;

/// <summary>
/// Thread-safe in-memory room. All state access is serialized through <see cref="_lock"/>;
/// operations return snapshots so callers don't hold the lock while broadcasting.
/// </summary>
public sealed class Room
{
    private readonly object _lock = new();
    private readonly List<RoomPeer> _peers = new();

    public Room(string id)
    {
        Id = id;
        CreatedAt = DateTime.UtcNow;
    }

    public string Id { get; }

    public DateTime CreatedAt { get; }

    public int PeerCount
    {
        get
        {
            lock (_lock) return _peers.Count;
        }
    }

    public RoomPeer[] SnapshotPeers()
    {
        lock (_lock) return _peers.ToArray();
    }

    /// <summary>Host is always the oldest peer; null when the room is empty.</summary>
    public Guid? HostPeerId
    {
        get
        {
            lock (_lock) return _peers.Count == 0 ? null : _peers[0].PeerId;
        }
    }

    public AddPeerResult TryAddPeer(RoomPeer peer, int maxCapacity)
    {
        lock (_lock)
        {
            if (_peers.Count >= maxCapacity)
            {
                return AddPeerResult.Full;
            }
            if (_peers.Any(p => p.PeerId == peer.PeerId))
            {
                return AddPeerResult.AlreadyIn;
            }
            _peers.Add(peer);
            var hostId = _peers[0].PeerId;
            var snapshot = _peers.ToArray();
            return AddPeerResult.Ok(hostId, snapshot);
        }
    }

    /// <summary>
    /// Flip a peer's streaming flag under the room lock. Returns true when the
    /// flag was mutated (i.e. the peer exists and the value actually changed),
    /// so the caller can decide whether to broadcast a StreamStarted/StreamEnded
    /// fan-out without racing a duplicate toggle from a second message.
    /// </summary>
    public bool TrySetPeerStreaming(Guid peerId, bool isStreaming)
    {
        lock (_lock)
        {
            var peer = _peers.FirstOrDefault(p => p.PeerId == peerId);
            if (peer is null) return false;
            if (peer.IsStreaming == isStreaming) return false;
            peer.IsStreaming = isStreaming;
            return true;
        }
    }

    /// <summary>
    /// Flip a peer's <see cref="RoomPeer.IsConnected"/> under the room lock. When
    /// transitioning to disconnected, stamps <see cref="RoomPeer.DisconnectedAtUtc"/>;
    /// when transitioning back to connected (resume), clears it. Returns true iff
    /// the flag actually changed, so the caller can broadcast
    /// <c>PeerConnectionState</c> exactly once.
    /// </summary>
    public bool TrySetPeerConnected(Guid peerId, bool isConnected)
    {
        lock (_lock)
        {
            var peer = _peers.FirstOrDefault(p => p.PeerId == peerId);
            if (peer is null) return false;
            if (peer.IsConnected == isConnected) return false;
            peer.IsConnected = isConnected;
            peer.DisconnectedAtUtc = isConnected ? null : DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Find a peer by their current resume token. Returns null if no peer holds
    /// this token (either never issued, or already rotated away by a prior
    /// successful resume).
    /// </summary>
    public RoomPeer? FindByResumeToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_lock)
        {
            return _peers.FirstOrDefault(p => p.ResumeToken == token);
        }
    }

    public RemovePeerResult RemovePeer(Guid peerId)
    {
        lock (_lock)
        {
            var idx = _peers.FindIndex(p => p.PeerId == peerId);
            if (idx < 0)
            {
                return new RemovePeerResult(false, false, null, _peers.Count);
            }
            var wasHost = idx == 0;
            _peers.RemoveAt(idx);
            var newHostId = _peers.Count == 0 ? (Guid?)null : _peers[0].PeerId;
            return new RemovePeerResult(true, wasHost, newHostId, _peers.Count);
        }
    }
}

public enum AddPeerStatus
{
    Ok,
    Full,
    AlreadyIn,
}

public readonly record struct AddPeerResult(
    AddPeerStatus Status,
    Guid HostPeerId,
    IReadOnlyList<RoomPeer> SnapshotAfterAdd)
{
    public static AddPeerResult Full { get; } = new(AddPeerStatus.Full, Guid.Empty, Array.Empty<RoomPeer>());
    public static AddPeerResult AlreadyIn { get; } = new(AddPeerStatus.AlreadyIn, Guid.Empty, Array.Empty<RoomPeer>());
    public static AddPeerResult Ok(Guid hostPeerId, IReadOnlyList<RoomPeer> snapshot) =>
        new(AddPeerStatus.Ok, hostPeerId, snapshot);
}

public readonly record struct RemovePeerResult(
    bool Found,
    bool WasHost,
    Guid? NewHostPeerId,
    int PeerCountAfter);
