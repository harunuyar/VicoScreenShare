namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvdec;

using VicoScreenShare.Client.Media.Codecs;
using Vortice.Direct3D11;

/// <summary>
/// H.264-only NVDEC factory. Construction is gated on
/// <see cref="NvDecCapabilities.IsH264Available"/>; on hosts without an
/// NVIDIA GPU (or with an NVIDIA GPU that somehow doesn't support H.264
/// — which we don't expect on any modern silicon), the factory reports
/// <see cref="IsAvailable"/> as false and the codec catalog falls back
/// to the Media Foundation H.264 MFT path.
///
/// Mirrors <see cref="NvDecAv1DecoderFactory"/>.
/// </summary>
public sealed class NvDecH264DecoderFactory : IVideoDecoderFactory
{
    private readonly ID3D11Device? _sharedDevice;

    public NvDecH264DecoderFactory()
    {
    }

    /// <summary>
    /// Bound to a caller-owned D3D11 device so the decoded BGRA texture
    /// lives on the same device as the renderer.
    /// </summary>
    public NvDecH264DecoderFactory(ID3D11Device sharedDevice)
    {
        _sharedDevice = sharedDevice;
    }

    public VideoCodec Codec => VideoCodec.H264;

    public bool IsAvailable => NvDecCapabilities.Probe().IsH264Available;

    public IVideoDecoder CreateDecoder() => new NvDecH264Decoder(_sharedDevice);

    public IVideoDecoder CreateDecoder(int width, int height)
        => new NvDecH264Decoder(_sharedDevice, width, height);
}
