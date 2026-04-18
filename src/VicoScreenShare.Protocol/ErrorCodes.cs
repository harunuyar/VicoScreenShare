namespace VicoScreenShare.Protocol;

public enum ErrorCode
{
    Unknown = 0,
    BadRequest = 1,
    ProtocolVersionMismatch = 2,
    /// <summary>ClientHello's AccessToken didn't match the server's configured AccessPassword.</summary>
    Unauthorized = 3,
    RoomNotFound = 10,
    RoomFull = 11,
    AlreadyInRoom = 13,
    NotInRoom = 14,
    UnknownPeer = 15,
    RateLimited = 20,
    InternalError = 99,
}
