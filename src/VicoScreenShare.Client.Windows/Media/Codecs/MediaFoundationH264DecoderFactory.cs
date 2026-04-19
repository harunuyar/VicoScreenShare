namespace VicoScreenShare.Client.Windows.Media.Codecs;

using VicoScreenShare.Client.Media.Codecs;
using Vortice.Direct3D11;

public sealed class MediaFoundationH264DecoderFactory : IVideoDecoderFactory
{
    private readonly ID3D11Device? _sharedDevice;

    /// <summary>Default: decoder creates sysmem-only output.</summary>
    public MediaFoundationH264DecoderFactory()
    {
    }

    /// <summary>
    /// Bound to a caller-owned D3D11 device so the decoder MFT can
    /// receive <c>SET_D3D_MANAGER</c> and emit D3D11 NV12 textures that
    /// stay on the GPU through the color-convert pipeline. Mirrors the
    /// encoder factory.
    /// </summary>
    public MediaFoundationH264DecoderFactory(ID3D11Device sharedDevice)
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

    public IVideoDecoder CreateDecoder() => new MediaFoundationH264Decoder(_sharedDevice);
}
