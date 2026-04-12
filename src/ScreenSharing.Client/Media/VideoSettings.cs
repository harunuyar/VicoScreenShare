using ScreenSharing.Client.Media.Codecs;

namespace ScreenSharing.Client.Media;

/// <summary>
/// User-configurable video pipeline settings. Drives the
/// <see cref="CaptureStreamer"/> resolution cap, the frame-rate throttle,
/// the codec choice, and (future) the encoder bitrate target. Persisted to
/// disk through <see cref="Services.SettingsStore"/> so the user's
/// preferences survive across app launches.
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
    public VideoCodec Codec { get; set; } = VideoCodec.Vp8;

    /// <summary>
    /// Largest width the encoder will accept. Captures bigger than this are
    /// aspect-preserved downscaled via <see cref="BgraDownscale.FitWithin"/>
    /// before encoding.
    /// </summary>
    public int MaxEncoderWidth { get; set; } = 1280;

    /// <summary>Same as <see cref="MaxEncoderWidth"/> for the height axis.</summary>
    public int MaxEncoderHeight { get; set; } = 720;

    /// <summary>
    /// Target frame rate in frames per second. CaptureStreamer drops any frame
    /// whose timestamp is closer than <c>1 / TargetFrameRate</c> seconds to the
    /// previously encoded frame.
    /// </summary>
    public int TargetFrameRate { get; set; } = 30;
}
