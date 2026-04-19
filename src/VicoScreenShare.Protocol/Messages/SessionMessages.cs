namespace VicoScreenShare.Protocol.Messages;

using System;

/// <summary>
/// First message sent by the client after the WebSocket opens. Establishes identity for the session.
/// <para>
/// <see cref="AccessToken"/> is the shared server password when the operator has configured
/// <c>RoomServerOptions.AccessPassword</c>; null otherwise. Defaulted so existing call sites
/// compile unchanged — the server only enforces a match when its own AccessPassword is set.
/// </para>
/// </summary>
public sealed record ClientHello(
    Guid UserId,
    string DisplayName,
    int ProtocolVersion,
    string? AccessToken = null);

public sealed record Ping(long Timestamp);

public sealed record Pong(long Timestamp);

public sealed record Error(ErrorCode Code, string Message);
