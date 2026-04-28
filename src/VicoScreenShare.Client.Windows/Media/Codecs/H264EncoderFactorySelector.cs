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
    private readonly MediaFoundationH264EncoderFactory _mft;
    private readonly NvencCapabilities _caps;

    /// <summary>
    /// Phase-1 gate. While false, the selector always returns the MFT path
    /// even when NVENC capabilities are present. Flip to true in Phase 2
    /// once <c>NvencH264Encoder</c> exists. Kept as a bool flag rather
    /// than removed so reverting is one line if a Phase-2 regression
    /// surfaces in production use.
    /// </summary>
    public static bool UseNvencSdk { get; set; } = false;

    public H264EncoderFactorySelector(ID3D11Device sharedDevice)
    {
        _mft = new MediaFoundationH264EncoderFactory(sharedDevice);
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
        // Phase 1: NVENC backend not implemented, always MFT.
        // Phase 2 will branch on UseNvencSdk && _caps.IsAvailable here.
        return _mft.CreateEncoder(width, height, targetFps, targetBitrate, gopFrames);
    }
}
