using ScreenSharing.Client.Media.Codecs;

namespace ScreenSharing.Client.Windows.Media.Codecs;

public sealed class MediaFoundationH264EncoderFactory : IVideoEncoderFactory
{
    public VideoCodec Codec => VideoCodec.H264;

    public bool IsAvailable
    {
        get
        {
            MediaFoundationRuntime.EnsureInitialized();
            return MediaFoundationRuntime.IsAvailable;
        }
    }

    public IVideoEncoder CreateEncoder(int width, int height, int targetFps, int targetBitrate) =>
        new MediaFoundationH264Encoder(width, height, targetFps, targetBitrate);
}
