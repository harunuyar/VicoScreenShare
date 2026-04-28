namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;

/// <summary>
/// User-facing knobs that the MFT shim no-ops or never exposed in the
/// first place. The encoder honors each only when
/// <see cref="NvencCapabilities"/> reports the corresponding bit as
/// supported on the user's hardware; unsupported requests are logged
/// once and silently dropped (the call still succeeds, just without
/// that feature).
///
/// Default = "off / preset default" so a fresh encoder constructed
/// without options behaves identically to the Phase-2 baseline. Settings
/// UI (or a test) flips these on explicitly.
/// </summary>
public sealed class NvencEncodeOptions
{
    /// <summary>
    /// Spatial AQ: redistribute QP toward complex regions (text/edges)
    /// at the expense of flat areas. The single-highest-ROI quality knob
    /// for screen content. Capability is universal on NVENC ≥ 9.x —
    /// no cap bit gates spatial AQ on its own.
    /// </summary>
    public bool EnableAdaptiveQuantization { get; init; }

    /// <summary>
    /// Temporal AQ: redistribute QP across frames in a GOP. Stacks with
    /// spatial AQ. Gated on <see cref="NvencCapabilities.SupportsTemporalAq"/>
    /// — Turing GPUs lack this; the encoder logs and falls back to
    /// spatial-only when probe says unsupported.
    /// </summary>
    public bool EnableTemporalAq { get; init; }

    /// <summary>
    /// AQ strength 1 (low) … 15 (aggressive). 0 = let the driver pick.
    /// Only meaningful when <see cref="EnableAdaptiveQuantization"/> is true.
    /// </summary>
    public int AqStrength { get; init; } = 8;

    /// <summary>
    /// Lookahead depth in frames. 0 = disabled. Adds encode-side latency
    /// equal to the depth; gate behind a settings toggle if used in the
    /// real-time path. Gated on <see cref="NvencCapabilities.SupportsLookahead"/>.
    /// </summary>
    public int LookaheadDepth { get; init; }

    /// <summary>
    /// Enable cyclic intra-refresh: spread refresh across N frames instead
    /// of bursting one IDR every <c>gopFrames</c>. Mutually exclusive with
    /// periodic IDR — the encoder also sets gopLength = INFINITE_GOPLENGTH
    /// when intra-refresh is on (per SDK note line 1868). Gated on
    /// <see cref="NvencCapabilities.SupportsIntraRefresh"/>.
    /// </summary>
    public bool EnableIntraRefresh { get; init; }

    /// <summary>
    /// Number of frames between successive refresh cycles. 0 = use the
    /// configured GOP length.
    /// </summary>
    public int IntraRefreshPeriodFrames { get; init; }

    /// <summary>
    /// VBV buffer size in bits. 0 = preset default (which on real-time
    /// presets is roughly bitrate × 2 / fps). A smaller value enforces
    /// tighter per-frame caps at the cost of per-frame quality variance.
    /// Gated on <see cref="NvencCapabilities.SupportsCustomVbvBufferSize"/>.
    /// </summary>
    public int VbvBufferSizeBits { get; init; }

    public static readonly NvencEncodeOptions Default = new();
}
