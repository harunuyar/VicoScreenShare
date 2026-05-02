namespace VicoScreenShare.Client.Windows.Media.Codecs;

using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using Vortice.Direct3D11;

/// <summary>
/// AV1-only Media Foundation factory. Mirrors
/// <see cref="MediaFoundationH264EncoderFactory"/>, with one critical
/// difference: there is no software AV1 encoder MFT in stock Windows, so
/// <see cref="IsAvailable"/> is gated on a real <c>MFTEnumEx</c> probe
/// for any GPU vendor's AV1 hardware encoder MFT (NVIDIA, Intel Quick
/// Sync, or AMD AMF). On machines without one the factory reports
/// unavailable and the AV1 encoder selector hides the MFT option from
/// Settings.
///
/// The probe runs once per factory construction and is cached so the
/// codec catalog can ask <see cref="IsAvailable"/> repeatedly without
/// re-enumerating the registry.
/// </summary>
public sealed class MediaFoundationAv1EncoderFactory : IVideoEncoderFactory
{
    private readonly ID3D11Device? _sharedDevice;
    private readonly bool _hasEncoder;

    public MediaFoundationAv1EncoderFactory()
        : this(sharedDevice: null)
    {
    }

    /// <summary>
    /// Factory bound to an externally-owned D3D11 device. Use this
    /// overload when the capture source already owns a device and you
    /// want the encoder, scaler, and framepool to share it (required
    /// for the GPU texture fast path).
    /// </summary>
    public MediaFoundationAv1EncoderFactory(ID3D11Device? sharedDevice)
    {
        _sharedDevice = sharedDevice;

        MediaFoundationRuntime.EnsureInitialized();
        _hasEncoder = MediaFoundationRuntime.IsAvailable
                      && MediaFoundationAv1Encoder.HasAv1EncoderInstalled();

        DebugLog.Write(_hasEncoder
            ? "[mft-av1-factory] AV1 encoder MFT detected; backend available"
            : "[mft-av1-factory] no AV1 encoder MFT registered; backend hidden");
    }

    /// <summary>
    /// The scaler mode to use when the encoder downscales captured
    /// textures. Set from <see cref="VideoSettings.Scaler"/> before
    /// the first encoder is created.
    /// </summary>
    public ScalerMode Scaler { get; set; } = ScalerMode.Bilinear;

    public VideoCodec Codec => VideoCodec.Av1;

    public bool IsAvailable => _hasEncoder;

    public bool SupportsTextureInput => _sharedDevice is not null && _hasEncoder;

    public IVideoEncoder CreateEncoder(
        int width,
        int height,
        int targetFps,
        int targetBitrate,
        int gopFrames) =>
        new MediaFoundationAv1Encoder(
            width, height, targetFps, targetBitrate, gopFrames,
            useLanczos: Scaler == ScalerMode.Lanczos,
            externalDevice: _sharedDevice);
}
