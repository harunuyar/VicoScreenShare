namespace VicoScreenShare.Client.Windows.Media.Codecs;

using VicoScreenShare.Client.Media.Codecs;
using Vortice.Direct3D11;

/// <summary>
/// Decoder factory for AV1, parallel to <see cref="MediaFoundationH264DecoderFactory"/>.
/// Uses the Microsoft "AV1 Video Extension" MFT (or a hardware-vendor MFT
/// when present) — gated on whether Media Foundation can enumerate any
/// AV1 decoder at all. On systems without the AV1 Video Extension the
/// codec catalog falls through to H.264.
/// </summary>
public sealed class MediaFoundationAv1DecoderFactory : IVideoDecoderFactory
{
    private readonly ID3D11Device? _sharedDevice;

    public MediaFoundationAv1DecoderFactory()
    {
    }

    /// <summary>
    /// Bound to a caller-owned D3D11 device so the decoder MFT can receive
    /// <c>SET_D3D_MANAGER</c> and emit D3D11 NV12 textures that stay on
    /// the GPU through the color-convert pipeline. Mirrors the H.264
    /// decoder factory.
    /// </summary>
    public MediaFoundationAv1DecoderFactory(ID3D11Device sharedDevice)
    {
        _sharedDevice = sharedDevice;
    }

    public VideoCodec Codec => VideoCodec.Av1;

    public bool IsAvailable
    {
        get
        {
            MediaFoundationRuntime.EnsureInitialized();
            if (!MediaFoundationRuntime.IsAvailable)
            {
                return false;
            }
            return MediaFoundationAv1Decoder.HasAv1DecoderInstalled();
        }
    }

    public IVideoDecoder CreateDecoder() => new MediaFoundationAv1Decoder(_sharedDevice);
}
