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
    }

    /// <summary>Per-session id assigned by the server. Unique while the session lives.</summary>
    public Guid PeerId { get; }

    /// <summary>Stable client-provided identity from %AppData% profile.</summary>
    public Guid UserId { get; }

    public string DisplayName { get; }

    public DateTime JoinedAt { get; }

    public bool IsStreaming { get; set; }
}
