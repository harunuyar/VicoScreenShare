using ScreenSharing.Client.Media.Codecs;
using Vortice.Direct3D11;

namespace ScreenSharing.Client.Windows.Media.Codecs;

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

    public VideoCodec Codec => VideoCodec.H264;

    public bool IsAvailable
    {
        get
        {
            MediaFoundationRuntime.EnsureInitialized();
            return MediaFoundationRuntime.IsAvailable;
        }
    }

    /// <summary>
    /// Texture input is supported whenever the factory was built against a
    /// shared D3D11 device (the one the capture source also uses) AND the
    /// runtime has at least one hardware MFT available. The negotiated
    /// input format is BGRA; see <see cref="MediaFoundationH264Encoder"/>.
    /// </summary>
    public bool SupportsTextureInput => _sharedDevice is not null && IsAvailable;

    public IVideoEncoder CreateEncoder(int width, int height, int targetFps, int targetBitrate, int gopFrames) =>
        new MediaFoundationH264Encoder(width, height, targetFps, targetBitrate, gopFrames, _sharedDevice);
}
