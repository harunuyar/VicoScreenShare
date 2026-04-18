using System;
using System.Collections.Generic;

namespace VicoScreenShare.Protocol.Messages;

public sealed record CreateRoom();

public sealed record RoomCreated(string RoomId);

public sealed record JoinRoom(string RoomId);

/// <summary>
/// Client → server. Signals an intentional departure, so the server bypasses
/// the grace window and evicts the peer immediately. Without this, closing
/// the WebSocket alone is indistinguishable from a network drop and other
/// peers would see this peer as "disconnected" for the full grace period.
/// </summary>
public sealed record LeaveRoom();

/// <summary>
/// Sent to a peer immediately after they successfully join a room. Contains the snapshot of the roster.
/// <para>
/// <see cref="ResumeToken"/> is a single-use opaque token the client stores in memory and presents
/// later via <see cref="ResumeSession"/> if the WebSocket drops. <see cref="ResumeTtl"/> is the
/// grace window during which the server reserves the peer's slot.
/// </para>
/// </summary>
public sealed record RoomJoined(
    string RoomId,
    Guid YourPeerId,
    IReadOnlyList<PeerInfo> Peers,
    IReadOnlyList<IceServerConfig> IceServers,
    string ResumeToken = "",
    TimeSpan ResumeTtl = default);

public sealed record IceServerConfig(
    IReadOnlyList<string> Urls,
    string? Username,
    string? Credential);

/// <summary>Broadcast to all existing peers when a new peer joins.</summary>
public sealed record PeerJoined(PeerInfo Peer);

/// <summary>Broadcast when a peer disconnects or leaves the room.</summary>
public sealed record PeerLeft(Guid PeerId);

/// <summary>
/// Broadcast when a peer's WebSocket drops into the server-side grace period
/// (<c>IsConnected=false</c>) or when a previously-disconnected peer successfully
/// resumes (<c>IsConnected=true</c>). Viewers render a ghosted tile/chip while
/// disconnected so the UI distinguishes "gone forever" from "reconnecting."
/// </summary>
public sealed record PeerConnectionState(Guid PeerId, bool IsConnected);

/// <summary>
/// Client → server. Sent in place of <see cref="JoinRoom"/> during a resume
/// attempt — the client presents the <see cref="ResumeToken"/> it received on
/// the original <see cref="RoomJoined"/> and the <see cref="RoomId"/> it was
/// in. On success the server rebinds the new <c>WsSession</c> to the existing
/// <c>RoomPeer</c>, preserving <c>PeerId</c>, host status, and
/// <c>IsStreaming</c>. On failure the server sends <see cref="ResumeFailed"/>
/// and the client falls back to a fresh <see cref="JoinRoom"/>.
/// </summary>
public sealed record ResumeSession(string RoomId, string ResumeToken);

public enum ResumeFailedReason
{
    /// <summary>Unknown token — possibly already consumed by a prior resume.</summary>
    TokenUnknown = 0,

    /// <summary>Token was valid but the grace window elapsed before this resume arrived.</summary>
    Expired = 1,

    /// <summary>The room no longer exists (empty → garbage collected or an explicit delete).</summary>
    RoomGone = 2,
}

public sealed record ResumeFailed(ResumeFailedReason Reason);
