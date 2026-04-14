using System;
using System.Collections.Generic;

namespace ScreenSharing.Protocol.Messages;

public sealed record CreateRoom();

public sealed record RoomCreated(string RoomId);

public sealed record JoinRoom(string RoomId);

/// <summary>
/// Sent to a peer immediately after they successfully join a room. Contains the snapshot of the roster.
/// IceServers is populated in Phase 6; empty list in earlier phases.
/// </summary>
public sealed record RoomJoined(
    string RoomId,
    Guid YourPeerId,
    IReadOnlyList<PeerInfo> Peers,
    IReadOnlyList<IceServerConfig> IceServers);

public sealed record IceServerConfig(
    IReadOnlyList<string> Urls,
    string? Username,
    string? Credential);

/// <summary>Broadcast to all existing peers when a new peer joins.</summary>
public sealed record PeerJoined(PeerInfo Peer);

/// <summary>Broadcast when a peer disconnects or leaves. If the peer was host, <see cref="NewHostPeerId"/> is set.</summary>
public sealed record PeerLeft(Guid PeerId, Guid? NewHostPeerId);
