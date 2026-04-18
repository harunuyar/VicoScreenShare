namespace VicoScreenShare.Server.Config;

public sealed class RoomServerOptions
{
    /// <summary>Maximum number of peers allowed in a single room.</summary>
    public int MaxRoomCapacity { get; set; } = 16;

    /// <summary>Global cap on concurrent rooms (DoS protection).</summary>
    public int MaxTotalRooms { get; set; } = 500;

    /// <summary>Server-side ping interval for heartbeat.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Drop the connection if no pong arrives within this window.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When a peer's WebSocket drops, keep their room slot reserved for this
    /// long so a reconnecting client can bind to the same <c>PeerId</c>,
    /// preserve host status, and re-emit <c>StreamStarted</c> with its
    /// existing stream id. After this window the peer is fully removed.
    /// </summary>
    public TimeSpan PeerGracePeriod { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Optional shared password clients must present in
    /// <c>ClientHello.AccessToken</c>. Null or empty = open server (no auth).
    /// Set this in the operator's <c>appsettings.json</c> under the
    /// <c>Rooms</c> section to restrict the server to friends who have
    /// the password.
    /// </summary>
    public string? AccessPassword { get; set; } = null;
}
