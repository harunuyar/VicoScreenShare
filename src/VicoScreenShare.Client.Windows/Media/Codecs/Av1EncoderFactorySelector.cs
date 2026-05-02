namespace VicoScreenShare.Client.Windows.Media.Codecs;

using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;
using Vortice.Direct3D11;

/// <summary>
/// Composite AV1 encoder factory: prefers the direct NVENC SDK path on
/// RTX 40+ silicon (gives us temporal AQ / lookahead / intra-refresh /
/// custom VBV), falls back to whatever AV1 encoder MFT the GPU driver
/// has registered otherwise (NVIDIA's MFT shim, Intel Quick Sync on
/// Arc / Xe2, or AMD AMF on RDNA 3+). Single registration touchpoint
/// at <c>App.OnStartup</c> — callers see one
/// <see cref="IVideoEncoderFactory"/> for AV1 regardless of which backend
/// ends up servicing each construction request.
///
/// Mirrors <see cref="H264EncoderFactorySelector"/> on the H.264 side and
/// <see cref="Av1DecoderFactorySelector"/> on the AV1 decode side.
/// Per-instance <see cref="Backend"/> reflects user intent from
/// <see cref="VideoSettings.Av1Backend"/>:
/// <list type="bullet">
///   <item><description><see cref="Av1EncoderBackend.Auto"/> — NVENC when
///   available, MFT otherwise. Default.</description></item>
///   <item><description><see cref="Av1EncoderBackend.NvencSdk"/> — try NVENC
///   first; fall back to MFT on construction failure so the toggle never
///   breaks the share.</description></item>
///   <item><description><see cref="Av1EncoderBackend.Mft"/> — try MFT; fall
///   back to NVENC if no MFT AV1 transform is registered, so picking this
///   on a pure-NVIDIA box still works.</description></item>
/// </list>
///
/// <see cref="IsAvailable"/> is true when EITHER backend has an encoder
/// available; the codec catalog uses this to gate AV1 visibility, so a
/// Quick Sync / AMF box gets AV1 in the codec dropdown even without NVENC.
/// </summary>
public sealed class Av1EncoderFactorySelector : IVideoEncoderFactory, IVideoEncoderDimensionPolicy
{
    private readonly NvencAv1EncoderFactory _nvenc;
    private readonly MediaFoundationAv1EncoderFactory _mft;

    /// <summary>
    /// Per-instance backend preference, set by the room view-model from
    /// <see cref="VideoSettings.Av1Backend"/>.
    /// </summary>
    public Av1EncoderBackend Backend { get; set; } = Av1EncoderBackend.Auto;

    /// <summary>True when the resolved backend (per <see cref="Backend"/>
    /// against capabilities) is the NVENC SDK path. Used by the Settings
    /// UI to gate the NVENC-only quality knobs (AQ / lookahead /
    /// intra-refresh / VBV).</summary>
    public bool IsNvencActive => ResolveBackend() == Av1EncoderBackend.NvencSdk;

    private Av1EncoderBackend ResolveBackend() => Backend switch
    {
        Av1EncoderBackend.Mft => _mft.IsAvailable ? Av1EncoderBackend.Mft :
                                  _nvenc.IsAvailable ? Av1EncoderBackend.NvencSdk : Av1EncoderBackend.Mft,
        Av1EncoderBackend.NvencSdk => _nvenc.IsAvailable ? Av1EncoderBackend.NvencSdk :
                                       _mft.IsAvailable ? Av1EncoderBackend.Mft : Av1EncoderBackend.NvencSdk,
        _ => _nvenc.IsAvailable ? Av1EncoderBackend.NvencSdk :
             _mft.IsAvailable ? Av1EncoderBackend.Mft : Av1EncoderBackend.NvencSdk, // Auto
    };

    public Av1EncoderFactorySelector(ID3D11Device sharedDevice)
    {
        _nvenc = new NvencAv1EncoderFactory(sharedDevice);
        _mft = new MediaFoundationAv1EncoderFactory(sharedDevice);

        DebugLog.Write($"[av1-encoder-select] backends: nvenc={(_nvenc.IsAvailable ? 1 : 0)} mft={(_mft.IsAvailable ? 1 : 0)}");
    }

    /// <summary>
    /// NVENC quality-knob options (AQ, lookahead, intra-refresh, VBV).
    /// Apply before constructing a new encoder; mid-stream changes have no
    /// effect until the next <see cref="CreateEncoder"/> call. Routed to
    /// the underlying NVENC factory; ignored on the MFT path.
    /// </summary>
    public NvencEncodeOptions NvencOptions
    {
        get => _nvenc.Options;
        set => _nvenc.Options = value;
    }

    /// <summary>
    /// Forwards through to the underlying MFT factory's scaler setting.
    /// The NVENC backend scales on the GPU using NVENC's own pre-processing,
    /// so this property only flows to the MFT path.
    /// </summary>
    public ScalerMode Scaler
    {
        get => _mft.Scaler;
        set => _mft.Scaler = value;
    }

    public VideoCodec Codec => VideoCodec.Av1;

    /// <summary>
    /// True when at least one backend can encode AV1. The codec catalog
    /// reads this to decide whether to surface AV1 in the codec dropdown.
    /// </summary>
    public bool IsAvailable => _nvenc.IsAvailable || _mft.IsAvailable;

    public bool SupportsTextureInput => IsNvencActive
        ? _nvenc.SupportsTextureInput
        : _mft.SupportsTextureInput;

    /// <summary>
    /// AV1 encoders pad to 8-pixel boundaries internally. NVENC requires
    /// macroblock-aligned dimensions; the MFT path is more permissive but
    /// padded coded dimensions different from the requested ones still
    /// confuse the receiver-side renderer. Keep alignment on for both
    /// backends so the contract is uniform regardless of the resolved
    /// path.
    /// </summary>
    public bool RequiresMacroblockAlignedDimensions => true;

    public IVideoEncoder CreateEncoder(
        int width,
        int height,
        int targetFps,
        int targetBitrate,
        int gopFrames)
    {
        var resolved = ResolveBackend();
        if (resolved == Av1EncoderBackend.NvencSdk && _nvenc.IsAvailable)
        {
            try
            {
                var enc = _nvenc.CreateEncoder(width, height, targetFps, targetBitrate, gopFrames);
                DebugLog.Write($"[av1-encoder-select] picked NVENC SDK ({width}x{height}@{targetFps} {targetBitrate} bps, pref={Backend})");
                return enc;
            }
            catch (NvencException ex)
            {
                DebugLog.Write($"[av1-encoder-select] NVENC ctor failed, falling back to MFT: {ex.Message}");
            }
        }

        if (_mft.IsAvailable)
        {
            DebugLog.Write($"[av1-encoder-select] picked MFT ({width}x{height}@{targetFps} {targetBitrate} bps, pref={Backend})");
            return _mft.CreateEncoder(width, height, targetFps, targetBitrate, gopFrames);
        }

        // Last-ditch: MFT not registered AND NVENC ctor failed (or pref
        // forced MFT on a non-MFT machine). Try NVENC even if it wasn't
        // the resolved choice — the user gets *some* AV1 stream rather
        // than a hard failure.
        if (_nvenc.IsAvailable)
        {
            DebugLog.Write($"[av1-encoder-select] last-resort NVENC ({width}x{height}@{targetFps} {targetBitrate} bps, pref={Backend})");
            return _nvenc.CreateEncoder(width, height, targetFps, targetBitrate, gopFrames);
        }

        throw new System.InvalidOperationException(
            "AV1 encoder selector has no available backend (neither NVENC AV1 silicon nor an AV1 encoder MFT detected)");
    }
}
