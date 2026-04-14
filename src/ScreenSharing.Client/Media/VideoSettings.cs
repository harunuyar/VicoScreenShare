using ScreenSharing.Client.Media.Codecs;

namespace ScreenSharing.Client.Media;

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
    /// Scaling filter used when <see cref="TargetHeight"/> is smaller than
    /// the source. Nearest is fastest but produces shimmering/stairstepped
    /// text. Bilinear is the default — cheap on GPU, good enough for most
    /// content. Bicubic and Lanczos give sharper text at a slightly higher
    /// GPU cost; their availability depends on the Video Processor caps of
    /// the host GPU, with automatic fallback to bilinear.
    /// </summary>
    public ScalerQuality ScalerQuality { get; set; } = ScalerQuality.Bilinear;

    /// <summary>
    /// How many frames the receiver buffers before starting to paint and
    /// after every underflow. Each frame of buffer adds one frame's worth
    /// of latency (16.67 ms at 60 fps) but absorbs that much network or
    /// decoder jitter. 3 frames = ~50 ms latency at 60 fps, which is a
    /// reasonable default for a low-RTT LAN/loopback session. Bump this
    /// up for high-jitter networks; drop to 1 if you want minimum latency
    /// and the network is rock solid.
    /// </summary>
    public int ReceiveBufferFrames { get; set; } = 3;
}

/// <summary>
/// Downscale filter quality. Ordered cheapest-to-sharpest. The D3D11 Video
/// Processor picks the best available filter on the host GPU and falls
/// back to <see cref="Bilinear"/> if the requested mode is unsupported.
/// </summary>
public enum ScalerQuality
{
    Nearest = 0,
    Bilinear = 1,
    Bicubic = 2,
    Lanczos = 3,
}
