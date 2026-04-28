namespace VicoScreenShare.Client.Windows.Media.Codecs.Nvenc;

using VicoScreenShare.Client.Media.Codecs;
using Vortice.Direct3D11;

/// <summary>
/// NVENC-only factory. Construction is gated on <see cref="NvencCapabilities.IsAvailable"/>;
/// the composite <see cref="H264EncoderFactorySelector"/> is what callers
/// actually register, so on non-NVIDIA hosts this factory is never reached.
/// </summary>
public sealed class NvencH264EncoderFactory : IVideoEncoderFactory
{
    private readonly ID3D11Device _sharedDevice;

    public NvencH264EncoderFactory(ID3D11Device sharedDevice)
    {
        _sharedDevice = sharedDevice;
    }

    public VideoCodec Codec => VideoCodec.H264;

    public bool IsAvailable => NvencCapabilities.Probe(_sharedDevice).IsAvailable;

    public bool SupportsTextureInput => IsAvailable;

    public IVideoEncoder CreateEncoder(int width, int height, int targetFps, int targetBitrate, int gopFrames) =>
        new NvencH264Encoder(width, height, targetFps, targetBitrate, gopFrames, _sharedDevice);
}
