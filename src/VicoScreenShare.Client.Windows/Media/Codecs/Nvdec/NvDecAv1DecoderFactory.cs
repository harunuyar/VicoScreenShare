namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvdec;

using VicoScreenShare.Client.Media.Codecs;
using Vortice.Direct3D11;

/// <summary>
/// AV1-only NVDEC factory. Construction is gated on
/// <see cref="NvDecCapabilities.IsAv1Available"/>; on hosts without
/// NVDEC AV1 silicon (anything older than RTX 30 / Volta), the factory
/// reports <see cref="IsAvailable"/> as false and the codec catalog
/// falls back to the Microsoft AV1 MFT path.
///
/// Mirrors <see cref="MediaFoundationAv1DecoderFactory"/> on the receive
/// side and <see cref="Nvenc.NvencAv1EncoderFactory"/> on the send side.
/// </summary>
public sealed class NvDecAv1DecoderFactory : IVideoDecoderFactory
{
    private readonly ID3D11Device? _sharedDevice;

    public NvDecAv1DecoderFactory()
    {
    }

    /// <summary>
    /// Bound to a caller-owned D3D11 device so the decoded BGRA texture
    /// lives on the same device as the renderer. Mirrors the H.264 /
    /// MFT AV1 factories.
    /// </summary>
    public NvDecAv1DecoderFactory(ID3D11Device sharedDevice)
    {
        _sharedDevice = sharedDevice;
    }

    public VideoCodec Codec => VideoCodec.Av1;

    public bool IsAvailable => NvDecCapabilities.Probe().IsAv1Available;

    public IVideoDecoder CreateDecoder() => new NvDecAv1Decoder(_sharedDevice);

    public IVideoDecoder CreateDecoder(int width, int height)
        => new NvDecAv1Decoder(_sharedDevice, width, height);
}
