namespace VicoScreenShare.Protocol.Messages;

using System;

/// <summary>
/// SDP offer routed through the server. Two flows use the same message:
/// <list type="bullet">
/// <item>Client-initiated, <see cref="SubscriptionId"/> = null — negotiates the client's
/// "main" peer connection (used for its own outbound screen share).</item>
/// <item>Server-initiated, <see cref="SubscriptionId"/> = the publisher's PeerId as
/// string-N format — negotiates a per-publisher subscriber peer connection. The server
/// creates one such PC per (viewer, publisher) pair so fan-out happens on dedicated
/// tracks with dedicated SSRCs; the viewer answers by building a matching RecvOnly PC.</item>
/// </list>
/// </summary>
public sealed record SdpOffer(string Sdp, string? SubscriptionId = null);

public sealed record SdpAnswer(string Sdp, string? SubscriptionId = null);

public sealed record IceCandidate(
    string Candidate,
    string? SdpMid,
    int? SdpMLineIndex,
    string? SubscriptionId = null);

public enum StreamKind
{
    Screen = 0,
    Application = 1,
}

/// <summary>
/// Announced by a peer when they begin sharing a video source. The server allocates a stream id and
/// broadcasts this message to other peers so they can prepare to receive the track.
/// <para>
/// <see cref="NominalFrameRate"/> is the cadence the sender's pace thread will emit frames at.
/// The receiver uses it to size its jitter buffer and to set its own paint clock to the same
/// rate, so wall-clock spacing between painted frames matches the spacing between sent frames.
/// </para>
/// </summary>
public sealed record StreamStarted(
    Guid PeerId,
    string StreamId,
    StreamKind Kind,
    bool HasAudio,
    int NominalFrameRate);

public sealed record StreamEnded(Guid PeerId, string StreamId);

/// <summary>
/// Sent by a viewer when their decoder can't produce output (e.g. joined mid-stream). The server
/// forwards a PLI to the publisher, who will emit an IDR frame.
/// </summary>
public sealed record RequestKeyframe(string StreamId);

/// <summary>
/// Client → server. Opt this viewer into receiving a specific publisher's stream.
/// Normally viewers are auto-subscribed on StreamStarted; this message covers the
/// resume-after-StopWatching case, where the user previously called
/// <see cref="Unsubscribe"/> and now wants the stream back. Server responds by
/// creating a new <c>SfuSubscriberPeer</c> and driving a fresh SDP offer.
/// </summary>
public sealed record Subscribe(Guid PublisherPeerId);

/// <summary>
/// Client → server. Stop receiving this publisher's stream. Server tears down
/// the matching <c>SfuSubscriberPeer</c> so the viewer stops paying for decode
/// bandwidth. Opt-out is per-stream-session — when the publisher stops and
/// starts again, every viewer auto-subscribes again.
/// </summary>
public sealed record Unsubscribe(Guid PublisherPeerId);

/// <summary>
/// Server → publisher. Periodic aggregate of the WORST fraction-lost across all
/// of this publisher's downstream subscribers, as observed on the SFU's own RR
/// feedback from each viewer's PC. The publisher's adaptive-bitrate controller
/// treats this as an additional loss input (separate from its own upstream RR
/// loss), which is how a self-hosted SFU addresses the case where the
/// publisher→server hop is clean but one subscriber's link can't keep up.
/// Value is in <c>[0.0, 1.0]</c>.
/// </summary>
public sealed record DownstreamLossReport(
    Guid PublisherPeerId,
    double FractionLost);
