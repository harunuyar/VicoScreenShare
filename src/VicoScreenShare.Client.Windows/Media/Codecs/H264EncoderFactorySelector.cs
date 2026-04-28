namespace VicoScreenShare.Client.Windows.Media.Codecs;

using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;
using Vortice.Direct3D11;

/// <summary>
/// Composite H.264 encoder factory: prefers a direct NVENC SDK path when
/// the user's GPU supports it, falls back to the Media Foundation MFT path
/// otherwise. Single registration touchpoint at <c>App.OnStartup</c> —
/// callers see one <see cref="IVideoEncoderFactory"/> for H.264 regardless
/// of which backend ends up servicing each construction request.
///
/// Phase 1: the NVENC encoder itself isn't built yet, so this class wires
/// the capability probe and the fallback path but always returns an MFT
/// encoder. Phase 2 swaps in <c>NvencH264Encoder</c> behind the
/// <see cref="UseNvencSdk"/> gate; Phase 3 enables the gate by default
/// when capabilities are present.
/// </summary>
public sealed class H264EncoderFactorySelector : IVideoEncoderFactory
{
    private readonly ID3D11Device _sharedDevice;
    private readonly MediaFoundationH264EncoderFactory _mft;
    private readonly NvencH264EncoderFactory _nvenc;
    private readonly NvencCapabilities _caps;

    /// <summary>
    /// Switch between NVENC SDK and the MFT path. Default off in Phase 2 so
    /// the encoder is opt-in for testing — flip to true to route H.264
    /// construction through <see cref="NvencH264Encoder"/> when capabilities
    /// allow. Phase 3 will default this to true once we've validated the
    /// quality knobs land via bench scenarios.
    /// </summary>
    public static bool UseNvencSdk { get; set; } = true;

    public H264EncoderFactorySelector(ID3D11Device sharedDevice)
    {
        _sharedDevice = sharedDevice;
        _mft = new MediaFoundationH264EncoderFactory(sharedDevice);
        _nvenc = new NvencH264EncoderFactory(sharedDevice);
        _caps = NvencCapabilities.Probe(sharedDevice);

        if (_caps.IsAvailable)
        {
            DebugLog.Write(
                $"[encoder-select] NVENC available "
                + $"(temporal_aq={(_caps.SupportsTemporalAq ? 1 : 0)} "
                + $"lookahead={(_caps.SupportsLookahead ? 1 : 0)} "
                + $"intra_refresh={(_caps.SupportsIntraRefresh ? 1 : 0)})");
        }
        else
        {
            DebugLog.Write($"[encoder-select] NVENC unavailable: {_caps.UnavailableReason}");
        }
    }

    /// <summary>Diagnostic surface for the Settings UI tooltip.</summary>
    public NvencCapabilities Capabilities => _caps;

    /// <summary>
    /// Forwards through to the underlying MFT factory's scaler setting.
    /// The NVENC backend, when it lands, will scale on the GPU using
    /// NVENC's own pre-processing instead — so this property only flows
    /// to the MFT path for now.
    /// </summary>
    public ScalerMode Scaler
    {
        get => _mft.Scaler;
        set => _mft.Scaler = value;
    }

    public VideoCodec Codec => VideoCodec.H264;

    public bool IsAvailable => _mft.IsAvailable || _caps.IsAvailable;

    public bool SupportsTextureInput =>
        (UseNvencSdk && _caps.IsAvailable)
        || _mft.SupportsTextureInput;

    public IVideoEncoder CreateEncoder(
        int width,
        int height,
        int targetFps,
        int targetBitrate,
        int gopFrames)
    {
        if (UseNvencSdk && _caps.IsAvailable)
        {
            try
            {
                var enc = _nvenc.CreateEncoder(width, height, targetFps, targetBitrate, gopFrames);
                DebugLog.Write($"[encoder-select] picked NVENC SDK ({width}x{height}@{targetFps} {targetBitrate} bps)");
                return enc;
            }
            catch (NvencException ex)
            {
                // Construction races (driver update, session-cap exhaustion,
                // RegisterResource cross-adapter failure) fall back cleanly.
                DebugLog.Write($"[encoder-select] NVENC ctor failed, falling back to MFT: {ex.Message}");
            }
        }
        return _mft.CreateEncoder(width, height, targetFps, targetBitrate, gopFrames);
    }
}
