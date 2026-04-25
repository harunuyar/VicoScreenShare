namespace VicoScreenShare.Client.Windows.Media.Codecs;

using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using Vortice.Direct3D11;

public sealed class MediaFoundationH264EncoderFactory : IVideoEncoderFactory
{
    private readonly ID3D11Device? _sharedDevice;

    /// <summary>Default factory — the encoder creates its own D3D11 device.</summary>
    public MediaFoundationH264EncoderFactory()
    {
    }

    /// <summary>
    /// Factory bound to an externally-owned D3D11 device. Use this overload
    /// when the capture source already owns a device and you want the
    /// encoder, scaler, and framepool to share it (required for zero-copy
    /// texture handoff).
    /// </summary>
    public MediaFoundationH264EncoderFactory(ID3D11Device sharedDevice)
    {
        _sharedDevice = sharedDevice;
    }

    /// <summary>
    /// The scaler mode to use when the encoder downscales captured
    /// textures. Set from <see cref="VideoSettings.Scaler"/> before
    /// the first encoder is created.
    /// </summary>
    public ScalerMode Scaler { get; set; } = ScalerMode.Bilinear;

    public VideoCodec Codec => VideoCodec.H264;

    public bool IsAvailable
    {
        get
        {
            MediaFoundationRuntime.EnsureInitialized();
            return MediaFoundationRuntime.IsAvailable;
        }
    }

    public bool SupportsTextureInput => _sharedDevice is not null && IsAvailable;

    public IVideoEncoder CreateEncoder(
        int width,
        int height,
        int targetFps,
        int targetBitrate,
        int gopFrames) =>
        new MediaFoundationH264Encoder(
            width, height, targetFps, targetBitrate, gopFrames,
            useLanczos: Scaler == ScalerMode.Lanczos,
            externalDevice: _sharedDevice);
}
