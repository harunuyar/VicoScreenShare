namespace VicoScreenShare.Server.Signaling;

using System.Collections.Concurrent;

/// <summary>
/// Global registry of active <see cref="WsSession"/> instances, keyed by peer id.
/// Used by <see cref="WsSession"/> to broadcast to other peers in the same room.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<Guid, WsSession> _sessions = new();

    public void Add(WsSession session) => _sessions[session.PeerId] = session;

    public bool Remove(Guid peerId) => _sessions.TryRemove(peerId, out _);

    public WsSession? Get(Guid peerId) =>
        _sessions.TryGetValue(peerId, out var s) ? s : null;

    public int Count => _sessions.Count;
}
