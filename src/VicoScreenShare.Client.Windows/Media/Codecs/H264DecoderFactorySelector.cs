namespace VicoScreenShare.Client.Windows.Media.Codecs;

using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Media.Codecs.Nvdec;
using Vortice.Direct3D11;

/// <summary>
/// Composite H.264 decoder factory: prefers NVDEC (direct cuvid driver
/// path) when the viewer's GPU supports it, falls back to the Media
/// Foundation H.264 MFT decoder otherwise. Single registration touchpoint
/// at <c>App.OnStartup</c> — the codec catalog sees one
/// <see cref="IVideoDecoderFactory"/> for H.264 regardless of which
/// backend ends up servicing each <c>StreamReceiver</c>'s decoder.
///
/// Mirrors <see cref="Av1DecoderFactorySelector"/> structurally; per-
/// instance <see cref="Backend"/> reflects user intent from
/// <see cref="VideoSettings.H264DecoderBackend"/>:
/// <list type="bullet">
///   <item><description><see cref="H264DecoderBackend.Auto"/> — NVDEC
///   when available, MFT otherwise. Default.</description></item>
///   <item><description><see cref="H264DecoderBackend.Nvdec"/> — try
///   NVDEC first; fall back to MFT if construction fails so the toggle
///   can never break the share.</description></item>
///   <item><description><see cref="H264DecoderBackend.Mft"/> — always
///   use MFT, even on NVDEC-capable hardware.</description></item>
/// </list>
/// </summary>
public sealed class H264DecoderFactorySelector : IVideoDecoderFactory
{
    private readonly NvDecH264DecoderFactory _nvdec;
    private readonly MediaFoundationH264DecoderFactory _mft;

    /// <summary>
    /// User backend preference, set from <see cref="VideoSettings.H264DecoderBackend"/>.
    /// Mid-stream changes don't affect already-running decoders; takes
    /// effect on the next <c>StreamReceiver</c> construction.
    /// </summary>
    public H264DecoderBackend Backend { get; set; } = H264DecoderBackend.Auto;

    /// <summary>True when the resolved backend (per <see cref="Backend"/>
    /// against capabilities) is the NVDEC path.</summary>
    public bool IsNvdecActive => ResolveBackend() == H264DecoderBackend.Nvdec;

    private H264DecoderBackend ResolveBackend() => Backend switch
    {
        H264DecoderBackend.Mft => H264DecoderBackend.Mft,
        H264DecoderBackend.Nvdec => _nvdec.IsAvailable ? H264DecoderBackend.Nvdec : H264DecoderBackend.Mft,
        _ => _nvdec.IsAvailable ? H264DecoderBackend.Nvdec : H264DecoderBackend.Mft, // Auto
    };

    public H264DecoderFactorySelector(ID3D11Device sharedDevice)
    {
        _nvdec = new NvDecH264DecoderFactory(sharedDevice);
        _mft = new MediaFoundationH264DecoderFactory(sharedDevice);

        DebugLog.Write($"[h264-decoder-select] backends: nvdec={(_nvdec.IsAvailable ? 1 : 0)} mft={(_mft.IsAvailable ? 1 : 0)}");
    }

    public VideoCodec Codec => VideoCodec.H264;

    /// <summary>
    /// True when at least one backend can decode H.264. MFT is always
    /// available on Windows (Microsoft's software H.264 decoder is
    /// universal); the codec is suppressed only if MF runtime fails to
    /// initialize at all.
    /// </summary>
    public bool IsAvailable => _nvdec.IsAvailable || _mft.IsAvailable;

    public IVideoDecoder CreateDecoder() => CreateDecoderInternal(width: 0, height: 0);

    public IVideoDecoder CreateDecoder(int width, int height) => CreateDecoderInternal(width, height);

    private IVideoDecoder CreateDecoderInternal(int width, int height)
    {
        if (ResolveBackend() == H264DecoderBackend.Nvdec)
        {
            try
            {
                var dec = (width > 0 && height > 0)
                    ? _nvdec.CreateDecoder(width, height)
                    : _nvdec.CreateDecoder();
                DebugLog.Write($"[h264-decoder-select] picked NVDEC (pref={Backend} hint={width}x{height})");
                return dec;
            }
            catch (System.Exception ex)
            {
                // NVDEC construction can fail at runtime even when the
                // probe said the silicon is present (driver session
                // exhaustion, CUDA context conflict, etc). Fall back
                // cleanly to MFT — same pattern as the AV1 selector.
                DebugLog.Write($"[h264-decoder-select] NVDEC ctor failed, falling back to MFT: {ex.GetType().Name}: {ex.Message}");
            }
        }
        DebugLog.Write($"[h264-decoder-select] picked MFT (pref={Backend} hint={width}x{height})");
        // MFT H.264 decoder learns dimensions from the bitstream's SPS
        // at first sample, so the hint is unused here. Calling the
        // no-arg form directly avoids needing the 2-arg overload on the
        // concrete factory.
        return _mft.CreateDecoder();
    }
}
