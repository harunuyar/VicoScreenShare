namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;

using VicoScreenShare.Client.Media.Codecs;
using Vortice.Direct3D11;

/// <summary>
/// AV1-only NVENC factory. Construction is gated on
/// <see cref="NvencCapabilities.IsAv1Available"/>; on hosts without AV1
/// hardware (anything older than RTX 40 / Intel Arc / AMD RDNA 3+), the
/// factory reports <see cref="IsAvailable"/> as false and the codec
/// catalog falls back to H.264.
///
/// <see cref="RequiresMacroblockAlignedDimensions"/> returns true: AV1
/// encoders pad to 8-pixel boundaries internally, but matching that on
/// the streamer side stops the receiver-side renderer from seeing
/// padded coded dimensions different from the requested ones.
/// </summary>
public sealed class NvencAv1EncoderFactory : IVideoEncoderFactory, IVideoEncoderDimensionPolicy
{
    private readonly ID3D11Device _sharedDevice;

    public NvencAv1EncoderFactory(ID3D11Device sharedDevice)
    {
        _sharedDevice = sharedDevice;
    }

    /// <summary>
    /// Per-encoder options. Mirrors <see cref="NvencH264EncoderFactory"/>.
    /// Settable while the factory is alive; each <see cref="CreateEncoder"/>
    /// call snapshots the current value.
    /// </summary>
    public NvencEncodeOptions Options { get; set; } = NvencEncodeOptions.Default;

    public VideoCodec Codec => VideoCodec.Av1;

    public bool IsAvailable => NvencCapabilities.Probe(_sharedDevice).IsAv1Available;

    public bool SupportsTextureInput => IsAvailable;

    public bool RequiresMacroblockAlignedDimensions => true;

    public IVideoEncoder CreateEncoder(int width, int height, int targetFps, int targetBitrate, int gopFrames) =>
        new NvencAv1Encoder(width, height, targetFps, targetBitrate, gopFrames, _sharedDevice, Options);
}
