using ScreenSharing.Client.Media.Codecs;

namespace ScreenSharing.Client.Windows.Media.Codecs;

public sealed class FFmpegH264DecoderFactory : IVideoDecoderFactory
{
    public VideoCodec Codec => VideoCodec.H264;

    public bool IsAvailable
    {
        get
        {
            FFmpegRuntime.EnsureInitialized();
            return FFmpegRuntime.IsAvailable;
        }
    }

    public IVideoDecoder CreateDecoder() => new FFmpegH264Decoder();
}
