namespace VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Opt-in cyclic intra-refresh. When <see cref="Enabled"/>, the encoder
/// spreads intra-coded macroblocks across <see cref="PeriodFrames"/> frames
/// instead of emitting a single big IDR every GOP — trading the ~250 Mbit
/// instantaneous keyframe spike for a small per-frame overhead. Only the
/// Media Foundation H.264 encoder honors this; software fallbacks and VP8
/// ignore the flag (their factories accept it for signature uniformity).
/// </summary>
public readonly record struct IntraRefreshOptions(bool Enabled, int PeriodFrames);

/// <summary>
/// Builds <see cref="IVideoEncoder"/> instances for one specific codec.
/// <see cref="IsAvailable"/> lets the settings UI disable a codec choice at
/// startup on machines where the backing encoder cannot be constructed — e.g.
/// an H.264 Media Foundation encoder on a machine without a matching GPU, or
/// AV1 HW encode on a GPU that does not support it.
/// </summary>
public interface IVideoEncoderFactory
{
    VideoCodec Codec { get; }

    bool IsAvailable { get; }

    /// <summary>
    /// True when encoders produced by this factory expose
    /// <see cref="IVideoEncoder.EncodeTexture"/> as a usable fast path.
    /// Callers query this up front so they can subscribe to the capture
    /// source's texture event instead of the CPU frame event, skipping
    /// the BGRA readback entirely on the GPU path.
    /// </summary>
    bool SupportsTextureInput { get; }

    /// <summary>
    /// Build an encoder at the given target dimensions and bitrate. The
    /// <paramref name="gopFrames"/> argument is the number of frames
    /// between keyframes (IDR), typically <c>round(keyframeIntervalSec *
    /// targetFps)</c>. Encoders that don't honor a configurable GOP
    /// (libvpx VP8 uses its own heuristics) accept the value and ignore it.
    /// <paramref name="intraRefresh"/> is default-off; when enabled,
    /// factories that support it configure the encoder for cyclic
    /// intra-refresh in place of scheduled IDRs.
    /// </summary>
    IVideoEncoder CreateEncoder(
        int width,
        int height,
        int targetFps,
        int targetBitrate,
        int gopFrames,
        IntraRefreshOptions intraRefresh = default);
}
