using ScreenSharing.Client.Media.Codecs;

namespace ScreenSharing.Client.Windows.Media.Codecs;

public sealed class MediaFoundationH264DecoderFactory : IVideoDecoderFactory
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

    public IVideoDecoder CreateDecoder() => new MediaFoundationH264Decoder();
}
