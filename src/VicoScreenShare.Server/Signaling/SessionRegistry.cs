namespace VicoScreenShare.Server.Signaling;

using System.Collections.Concurrent;
using System.Collections.Generic;

/// <summary>
/// Global registry of active <see cref="WsSession"/> instances, keyed by peer id.
/// Used by <see cref="WsSession"/> to broadcast to other peers in the same room.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<Guid, WsSession> _sessions = new();

    public void Add(WsSession session) => _sessions[session.PeerId] = session;

    /// <summary>
    /// Atomic compare-and-remove by <see cref="WsSession"/> identity. Removes
    /// the entry at <c>session.PeerId</c> ONLY if the currently-stored instance
    /// is the same object as <paramref name="session"/>. This is essential for
    /// the resume path: when a client reconnects, the new WsSession rebinds to
    /// the old peer id and adds itself to the registry; meanwhile the old
    /// session's reader loop is ending and will call Remove. A key-only remove
    /// would wipe the freshly-bound new session from the registry. Identity
    /// compare leaves the new session alone because the slot no longer holds
    /// the old instance.
    /// </summary>
    public bool Remove(WsSession session) =>
        ((ICollection<KeyValuePair<Guid, WsSession>>)_sessions)
            .Remove(new KeyValuePair<Guid, WsSession>(session.PeerId, session));

    public WsSession? Get(Guid peerId) =>
        _sessions.TryGetValue(peerId, out var s) ? s : null;

    public int Count => _sessions.Count;
}
