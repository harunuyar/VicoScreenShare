namespace VicoScreenShare.Client.Windows.Media.Codecs;

using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Media.Codecs.Nvdec;
using Vortice.Direct3D11;

/// <summary>
/// Composite AV1 decoder factory: prefers NVDEC (direct cuvid driver path)
/// when the viewer's GPU supports it, falls back to the Microsoft "AV1
/// Video Extension" MFT decoder otherwise. Single registration touchpoint
/// at <c>App.OnStartup</c> — the codec catalog sees one
/// <see cref="IVideoDecoderFactory"/> for AV1 regardless of which backend
/// ends up servicing each <c>StreamReceiver</c>'s decoder.
///
/// Mirrors <see cref="H264EncoderFactorySelector"/> on the encoder side.
/// Per-instance <see cref="Backend"/> reflects user intent from
/// <see cref="VideoSettings.Av1DecoderBackend"/>:
/// <list type="bullet">
///   <item><description><see cref="Av1DecoderBackend.Auto"/> — NVDEC when
///   available, MFT otherwise. Default.</description></item>
///   <item><description><see cref="Av1DecoderBackend.Nvdec"/> — try NVDEC
///   first; fall back to MFT if construction fails so the toggle can never
///   break the share.</description></item>
///   <item><description><see cref="Av1DecoderBackend.Mft"/> — always use
///   MFT, even on NVDEC-capable hardware.</description></item>
/// </list>
/// </summary>
public sealed class Av1DecoderFactorySelector : IVideoDecoderFactory
{
    private readonly NvDecAv1DecoderFactory _nvdec;
    private readonly MediaFoundationAv1DecoderFactory _mft;

    /// <summary>
    /// User backend preference, set from <see cref="VideoSettings.Av1DecoderBackend"/>.
    /// Mid-stream changes don't affect already-running decoders; takes
    /// effect on the next <c>StreamReceiver</c> construction (which
    /// happens on the next room join or codec switch).
    /// </summary>
    public Av1DecoderBackend Backend { get; set; } = Av1DecoderBackend.Auto;

    /// <summary>True when the resolved backend (per <see cref="Backend"/>
    /// against capabilities) is the NVDEC path.</summary>
    public bool IsNvdecActive => ResolveBackend() == Av1DecoderBackend.Nvdec;

    private Av1DecoderBackend ResolveBackend() => Backend switch
    {
        Av1DecoderBackend.Mft => Av1DecoderBackend.Mft,
        Av1DecoderBackend.Nvdec => _nvdec.IsAvailable ? Av1DecoderBackend.Nvdec : Av1DecoderBackend.Mft,
        _ => _nvdec.IsAvailable ? Av1DecoderBackend.Nvdec : Av1DecoderBackend.Mft, // Auto
    };

    public Av1DecoderFactorySelector(ID3D11Device sharedDevice)
    {
        _nvdec = new NvDecAv1DecoderFactory(sharedDevice);
        _mft = new MediaFoundationAv1DecoderFactory(sharedDevice);

        if (_nvdec.IsAvailable)
        {
            DebugLog.Write("[av1-decoder-select] NVDEC AV1 available");
        }
        else
        {
            DebugLog.Write("[av1-decoder-select] NVDEC AV1 unavailable; MFT fallback only");
        }
    }

    public VideoCodec Codec => VideoCodec.Av1;

    /// <summary>
    /// True when at least one backend can decode AV1 — either NVDEC
    /// silicon is present, or the Microsoft AV1 MFT decoder is
    /// installed. False on machines where neither holds, in which case
    /// the codec catalog suppresses AV1 entirely.
    /// </summary>
    public bool IsAvailable => _nvdec.IsAvailable || _mft.IsAvailable;

    public IVideoDecoder CreateDecoder() => CreateDecoderInternal(width: 0, height: 0);

    public IVideoDecoder CreateDecoder(int width, int height) => CreateDecoderInternal(width, height);

    private IVideoDecoder CreateDecoderInternal(int width, int height)
    {
        if (ResolveBackend() == Av1DecoderBackend.Nvdec)
        {
            try
            {
                var dec = (width > 0 && height > 0)
                    ? _nvdec.CreateDecoder(width, height)
                    : _nvdec.CreateDecoder();
                DebugLog.Write($"[av1-decoder-select] picked NVDEC (pref={Backend} hint={width}x{height})");
                return dec;
            }
            catch (System.Exception ex)
            {
                // NVDEC construction can fail at runtime even when the
                // probe said the silicon is present (driver session
                // exhaustion, CUDA context conflict, etc). Fall back
                // cleanly to MFT — same pattern as the encoder selector.
                DebugLog.Write($"[av1-decoder-select] NVDEC ctor failed, falling back to MFT: {ex.GetType().Name}: {ex.Message}");
            }
        }
        DebugLog.Write($"[av1-decoder-select] picked MFT (pref={Backend} hint={width}x{height})");
        return (width > 0 && height > 0)
            ? _mft.CreateDecoder(width, height)
            : _mft.CreateDecoder();
    }
}
