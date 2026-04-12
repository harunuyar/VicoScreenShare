using System;

namespace ScreenSharing.Protocol.Messages;

/// <summary>
/// First message sent by the client after the WebSocket opens. Establishes identity for the session.
/// </summary>
public sealed record ClientHello(
    Guid UserId,
    string DisplayName,
    int ProtocolVersion);

public sealed record Ping(long Timestamp);

public sealed record Pong(long Timestamp);

public sealed record Error(ErrorCode Code, string Message);
