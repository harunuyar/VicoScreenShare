namespace VicoScreenShare.Server.Config;

using System.Collections.Generic;

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

    /// <summary>
    /// ICE (STUN/TURN) servers advertised to every client at join time and
    /// used by the server's own SFU peer connections. Empty list falls back
    /// to Google's public STUN (<c>stun:stun.l.google.com:19302</c>) so
    /// deployments that don't touch this setting keep working — but any
    /// production deployment should configure its own STUN and, for users
    /// behind strict NATs, a TURN server.
    /// </summary>
    public List<IceServerOptions> IceServers { get; set; } = new();
}

/// <summary>
/// One operator-configured ICE server entry. Mirrors the wire-format
/// <c>IceServerConfig</c> in the protocol but lives under
/// <see cref="RoomServerOptions"/> so the operator sets it in
/// <c>appsettings.json</c> under <c>Rooms:IceServers</c>.
/// </summary>
public sealed class IceServerOptions
{
    /// <summary>
    /// One or more URLs for this entry. Usually a single STUN or TURN URL
    /// (e.g. <c>stun:stun.example.com:3478</c>,
    /// <c>turn:turn.example.com:3478?transport=udp</c>). WebRTC allows
    /// multiple URLs sharing the same credentials.
    /// </summary>
    public List<string> Urls { get; set; } = new();

    /// <summary>Username for TURN auth. Leave null for STUN-only entries.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// Shared-secret credential for TURN auth. Leave null for STUN-only
    /// entries. Static credentials are fine for friends-only deployments;
    /// production TURN should use time-limited credentials.
    /// </summary>
    public string? Credential { get; set; }
}
