namespace ScreenSharing.Protocol;

public enum ErrorCode
{
    Unknown = 0,
    BadRequest = 1,
    ProtocolVersionMismatch = 2,
    RoomNotFound = 10,
    RoomFull = 11,
    InvalidPassword = 12,
    AlreadyInRoom = 13,
    NotInRoom = 14,
    UnknownPeer = 15,
    RateLimited = 20,
    InternalError = 99,
}
