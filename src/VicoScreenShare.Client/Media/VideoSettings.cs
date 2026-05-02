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

    /// <summary>
    /// Master switch for the send-side packet pacer. When on, encoded
    /// frames are queued and their RTP packets are released to the wire
    /// at a rate of <see cref="TargetBitrate"/> ×
    /// <see cref="SendPacingBitrateMultiplier"/> instead of being dumped
    /// in a back-to-back burst. Smooths the keyframe spike — a 1 MB IDR
    /// stops overrunning a viewer's downlink router queue, at the cost
    /// of one-time per-keyframe latency proportional to (keyframe size /
    /// pace rate). The receiver's content-PTS pacer absorbs the latency
    /// after the first keyframe, so steady-state cadence is unchanged.
    /// Default off; flip on when a viewer on a slower link reports
    /// blocky / dropped video on every keyframe.
    /// </summary>
    public bool EnableSendPacing { get; set; } = false;

    /// <summary>
    /// Multiplier on <see cref="TargetBitrate"/> that sets the pacer's
    /// wire-rate cap. <c>1</c> = pace exactly at the encoder's target
    /// bitrate (smoothest, highest one-time keyframe latency).
    /// Higher values let the pacer release packets faster, reducing
    /// keyframe latency at the cost of larger wire bursts. Range
    /// <c>[1, 5]</c>. Ignored when <see cref="EnableSendPacing"/> is
    /// off.
    /// </summary>
    public int SendPacingBitrateMultiplier { get; set; } = 1;

    /// <summary>
    /// Which H.264 encoder backend the client uses. <see cref="H264EncoderBackend.Auto"/>
    /// (the default) picks NVENC SDK when the GPU supports it, falls back
    /// to Media Foundation otherwise. Force <see cref="H264EncoderBackend.Mft"/>
    /// to use the legacy Media Foundation H.264 encoder regardless of GPU
    /// (useful for A/B comparison or when working around an NVENC quirk).
    /// Force <see cref="H264EncoderBackend.NvencSdk"/> to require the
    /// direct path and refuse to fall back; if the GPU lacks support the
    /// selector will still fall through to MFT, but the toggle records
    /// the user's intent. Ignored when <see cref="Codec"/> is not H.264.
    /// </summary>
    public H264EncoderBackend H264Backend { get; set; } = H264EncoderBackend.Auto;

    /// <summary>
    /// Which AV1 decoder backend the client uses. <see cref="Av1DecoderBackend.Auto"/>
    /// (the default) picks NVDEC when the GPU supports it, falls back to
    /// the Microsoft "AV1 Video Extension" MFT decoder otherwise. Force
    /// <see cref="Av1DecoderBackend.Mft"/> to use the MFT path even on
    /// NVDEC-capable hardware (useful for A/B comparison or as a workaround
    /// if a driver regression breaks the cuvid path). Force
    /// <see cref="Av1DecoderBackend.Nvdec"/> to require the direct cuvid
    /// path; the selector still falls back to MFT if NVDEC isn't available
    /// so the toggle never breaks the share. Ignored when the negotiated
    /// codec is not AV1.
    /// </summary>
    public Av1DecoderBackend Av1DecoderBackend { get; set; } = Av1DecoderBackend.Auto;

    // ----- NVENC SDK quality knobs -----
    // These four properties drive the direct NVENC SDK encoder backend
    // when it's the active backend (Auto on NVIDIA, or explicit NvencSdk).
    // On the MFT path the values are read but ignored — that contract
    // doesn't expose AQ / lookahead / intra-refresh / VBV control.

    /// <summary>
    /// Spatial AQ: shift bit budget toward complex regions (text, edges)
    /// and away from flat areas. Single-highest-ROI quality knob for
    /// screen content. Default on. Disabled on the MFT path; greyed in
    /// Settings UI when <c>NvencCapabilities.IsAvailable</c> is false.
    /// </summary>
    public bool EnableAdaptiveQuantization { get; set; } = true;

    /// <summary>
    /// Encoder-internal lookahead: smarter rate-distortion decisions over
    /// a window of frames. Adds encoder-pipeline latency equal to the
    /// depth, so default off; flip on for "readability mode" use cases
    /// where the latency budget is softer than the visible-quality
    /// budget. Greyed when the GPU lacks <c>SupportsLookahead</c>.
    /// </summary>
    public bool EnableEncoderLookahead { get; set; } = false;

    /// <summary>
    /// Cyclic intra-refresh: replace periodic IDR keyframe bursts with
    /// gradual refresh slices spread over a window. Halves the worst-case
    /// per-frame size at GOP boundaries. Default on; flip off if a
    /// receiver decoder reports decode errors (rare). Greyed when the
    /// GPU lacks <c>SupportsIntraRefresh</c>.
    /// </summary>
    public bool EnableIntraRefresh { get; set; } = true;

    /// <summary>
    /// Number of frames between successive intra-refresh cycles. 0 =
    /// auto (use GOP length). Only meaningful when
    /// <see cref="EnableIntraRefresh"/> is true.
    /// </summary>
    public int IntraRefreshPeriodFrames { get; set; } = 0;

    /// <summary>
    /// NVENC preset level 1..7. P1 = fastest / lowest quality, P7 = slowest
    /// / highest quality. Default 4 = balanced. P5/P6 are typically a
    /// quality win on a 4070-class card without breaking the real-time
    /// budget; P7 is for archive-grade quality where encode time is no
    /// constraint. Ignored on the MFT path.
    /// </summary>
    public int NvencPreset { get; set; } = 4;
}

/// <summary>
/// Selects which H.264 encoder backend the client uses on Windows.
/// </summary>
public enum H264EncoderBackend
{
    /// <summary>
    /// Pick the NVENC SDK direct path on NVIDIA GPUs that support it; fall
    /// back to Media Foundation otherwise. Recommended default.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Force the legacy Media Foundation H.264 encoder. Universal — works
    /// on any Windows GPU. Lacks adaptive quantization, lookahead,
    /// intra-refresh, and custom VBV (the MFT contract doesn't expose
    /// them, and NVIDIA's MFT shim silently ignores knobs that DO exist).
    /// </summary>
    Mft = 1,

    /// <summary>
    /// Force the direct NVENC SDK path. NVIDIA GPUs only; the selector
    /// still falls back to MFT if construction fails (driver missing,
    /// session limit hit, etc.) so picking this never breaks the share.
    /// </summary>
    NvencSdk = 2,
}

/// <summary>
/// Selects which AV1 decoder backend the client uses on Windows. Decoder
/// selection is per-viewer (each StreamReceiver constructs its own decoder
/// from this preference); the publisher's encoder choice is independent.
/// </summary>
public enum Av1DecoderBackend
{
    /// <summary>
    /// Pick NVDEC (direct cuvid driver path) on hardware that supports
    /// it; fall back to the Microsoft "AV1 Video Extension" MFT decoder
    /// otherwise. Recommended default — NVDEC handles 4K AV1 IDRs in
    /// single-digit ms; MFT typically spends 30-45 ms and produces
    /// visible micro-stutters at IDR boundaries.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Force the Microsoft Media Foundation AV1 decoder. Universal
    /// (any GPU) but slower; useful for A/B comparison or as a manual
    /// workaround if NVDEC misbehaves on a specific driver. Requires the
    /// "AV1 Video Extension" Microsoft Store package on the viewer host.
    /// </summary>
    Mft = 1,

    /// <summary>
    /// Force the NVDEC direct cuvid path. RTX 30 / Volta+ NVIDIA only;
    /// the selector falls back to MFT if NVDEC isn't available, so
    /// picking this never breaks the share.
    /// </summary>
    Nvdec = 2,
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
