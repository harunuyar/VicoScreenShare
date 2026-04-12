namespace ScreenSharing.Client.Media;

/// <summary>
/// User-configurable video pipeline settings. Drives the
/// <see cref="CaptureStreamer"/> resolution cap, the frame-rate throttle, and
/// (future) the encoder bitrate target. Persisted to disk through
/// <see cref="Services.SettingsStore"/> so the user's preferences survive
/// across app launches.
/// </summary>
public sealed class VideoSettings
{
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
