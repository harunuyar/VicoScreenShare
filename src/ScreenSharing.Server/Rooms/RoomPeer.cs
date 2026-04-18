namespace ScreenSharing.Server.Rooms;

/// <summary>Server-side record of a peer inside a room.</summary>
public sealed class RoomPeer
{
    public RoomPeer(Guid peerId, Guid userId, string displayName)
    {
        PeerId = peerId;
        UserId = userId;
        DisplayName = displayName;
        JoinedAt = DateTime.UtcNow;
        IsConnected = true;
    }

    /// <summary>Per-session id assigned by the server. Unique while the session lives.</summary>
    public Guid PeerId { get; }

    /// <summary>Stable client-provided identity from %AppData% profile.</summary>
    public Guid UserId { get; }

    public string DisplayName { get; }

    public DateTime JoinedAt { get; }

    public bool IsStreaming { get; set; }

    /// <summary>
    /// False while the peer is inside the server-side grace window (WebSocket
    /// closed, slot reserved). Flipped back to true on successful resume, or
    /// the whole peer is removed from the room on grace-window expiry.
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Opaque, random token issued at join time and rotated on each successful
    /// resume. Null between issuances. Used to authenticate a <c>ResumeSession</c>
    /// attempt.
    /// </summary>
    public string? ResumeToken { get; set; }

    /// <summary>Wall-clock UTC instant the WebSocket dropped; null when connected.</summary>
    public DateTime? DisconnectedAtUtc { get; set; }

    /// <summary>
    /// If the peer was publishing at the moment their WS dropped, the stream id
    /// is captured here so the server can re-emit a <c>StreamStarted</c> on resume
    /// and viewers don't unmount their tile.
    /// </summary>
    public string? LastStreamId { get; set; }
}
