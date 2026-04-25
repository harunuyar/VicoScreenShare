namespace VicoScreenShare.Client.Media;

using VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// User-configurable video pipeline settings. Drives the encoder's target
/// resolution, frame rate, bitrate, keyframe interval, scaler quality and
/// codec. Persisted to disk through <see cref="Services.SettingsStore"/> so
/// the user's preferences survive across app launches.
///
/// The resolution model is "pick a target height, derive width from the
/// source aspect ratio" so the output never distorts the captured source.
/// <see cref="TargetHeight"/> = 0 means "use the source's own height" — i.e.
/// no downscaling.
/// </summary>
public sealed class VideoSettings
{
    /// <summary>
    /// The wire codec the client prefers for its outbound stream. VP8 is the
    /// universal baseline and is always available; H.264 / AV1 depend on
    /// whether the host has FFmpeg installed and which hardware encoders are
    /// supported on the GPU. Read at room-join time and not re-applied during
    /// an active session — changes take effect the next time you join a room.
    /// </summary>
    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    /// <summary>
    /// Target encoder height in pixels. Width is derived from the source
    /// aspect ratio at runtime so the output never distorts. 0 means "match
    /// the source height" (no downscale).
    /// </summary>
    public int TargetHeight { get; set; } = 1080;

    /// <summary>
    /// Target frame rate in frames per second. CaptureStreamer drops any frame
    /// whose timestamp is closer than <c>1 / TargetFrameRate</c> seconds to the
    /// previously encoded frame. Upper bound is 240 so a 240Hz display can be
    /// captured at native rate.
    /// </summary>
    public int TargetFrameRate { get; set; } = 60;

    /// <summary>
    /// Target encoder bitrate in bits per second. Hardware encoders treat
    /// this as an average / target; the rate controller may spike above it
    /// on keyframes.
    /// </summary>
    public int TargetBitrate { get; set; } = 12_000_000;

    /// <summary>
    /// Keyframe (GOP) interval in seconds. Shorter = faster new-viewer
    /// join-in but more bits spent on keyframes. 2s is a reasonable default
    /// for screen sharing where movement is bursty.
    /// </summary>
    public double KeyframeIntervalSeconds { get; set; } = 2.0;

    /// <summary>
    /// Downscale filter used when <see cref="TargetHeight"/> is smaller
    /// than the source. <see cref="ScalerMode.Bilinear"/> is the default
    /// — cheap on GPU, good for gaming and video. <see cref="ScalerMode.Lanczos"/>
    /// uses a Lanczos3 compute shader that preserves sharp text edges
    /// when downscaling (e.g. 1440p → 1080p) at the cost of more GPU
    /// work per frame. Use Lanczos for code sessions where readability
    /// matters more than frame rate.
    /// </summary>
    public ScalerMode Scaler { get; set; } = ScalerMode.Bilinear;

    /// <summary>
    /// How many frames the <see cref="TimestampedFrameQueue"/> on the
    /// receiver holds before the <see cref="PresentLoop"/> begins painting.
    /// Also the target steady-state depth the homeostasis feedback keeps
    /// the queue near. Each frame of buffer adds one frame's worth of
    /// latency (16.67 ms at 60 fps) but absorbs that much network or
    /// decoder jitter. Default 5 frames ≈ 83 ms latency at 60 fps, which
    /// is the standard low-latency WebRTC jitter buffer size. Range is
    /// <c>[1, 240]</c>: 1 = minimum latency for rock-solid LAN, 240 =
    /// high-jitter network or huge pre-roll for scrubby playback.
    /// </summary>
    public int ReceiveBufferFrames { get; set; } = 5;

    /// <summary>
    /// Master switch for RTCP-RR-driven adaptive bitrate. When on, the
    /// publisher reads the receiver's reported fraction-lost on inbound
    /// RTCP RRs and steps the encoder bitrate down on sustained loss
    /// (with gradual recovery up to <see cref="TargetBitrate"/> as loss
    /// clears). Turns "12 Mbps keyframe bursts into a 3 Mbps link" from
    /// a permanent stall into a self-correcting quality reduction.
    /// Default on; flip off for A/B troubleshooting.
    /// </summary>
    public bool EnableAdaptiveBitrate { get; set; } = true;

    /// <summary>
    /// Lower bound the adaptive bitrate controller is allowed to step down
    /// to. Stops the loop from driving quality into the floor on extremely
    /// bad links. Bits per second. Default 500 kbps.
    /// </summary>
    public int MinAdaptiveBitrate { get; set; } = 500_000;
}

/// <summary>
/// Downscale filter mode for the encoder's capture → encode path.
/// </summary>
public enum ScalerMode
{
    /// <summary>Fast GPU bilinear (D3D11 Video Processor). Good for
    /// gaming and video content. Default.</summary>
    Bilinear = 0,

    /// <summary>Lanczos3 compute shader. Produces sharp text at the
    /// cost of more GPU work per frame. Use for code sessions.</summary>
    Lanczos = 1,
}
