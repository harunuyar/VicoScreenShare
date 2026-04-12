using ScreenSharing.Client.Media.Codecs;

namespace ScreenSharing.Client.Windows.Media.Codecs;

public sealed class MediaFoundationH264EncoderFactory : IVideoEncoderFactory
{
    private readonly int _targetFps;
    private readonly long _targetBitrate;

    public MediaFoundationH264EncoderFactory(int targetFps = 30, long targetBitrate = 6_000_000)
    {
        _targetFps = targetFps;
        _targetBitrate = targetBitrate;
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

    public IVideoEncoder CreateEncoder(int width, int height) =>
        new MediaFoundationH264Encoder(width, height, _targetFps, _targetBitrate);
}
