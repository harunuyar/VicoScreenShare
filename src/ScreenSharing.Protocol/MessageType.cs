namespace ScreenSharing.Protocol;

public static class MessageType
{
    public const string ClientHello = "client_hello";
    public const string CreateRoom = "create_room";
    public const string RoomCreated = "room_created";
    public const string JoinRoom = "join_room";
    public const string LeaveRoom = "leave_room";
    public const string RoomJoined = "room_joined";
    public const string PeerJoined = "peer_joined";
    public const string PeerLeft = "peer_left";
    public const string SdpOffer = "sdp_offer";
    public const string SdpAnswer = "sdp_answer";
    public const string IceCandidate = "ice_candidate";
    public const string StreamStarted = "stream_started";
    public const string StreamEnded = "stream_ended";
    public const string RequestKeyframe = "request_keyframe";
    public const string Subscribe = "subscribe";
    public const string Unsubscribe = "unsubscribe";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Error = "error";
    public const string ResumeSession = "resume_session";
    public const string ResumeFailed = "resume_failed";
    public const string PeerConnectionState = "peer_connection_state";
}
