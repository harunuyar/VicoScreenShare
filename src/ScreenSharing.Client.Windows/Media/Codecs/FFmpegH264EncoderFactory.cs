using ScreenSharing.Client.Media.Codecs;

namespace ScreenSharing.Client.Windows.Media.Codecs;

/// <summary>
/// Factory for <see cref="FFmpegH264Encoder"/>. Reports unavailable when the
/// FFmpeg native libraries cannot be loaded; the Desktop host checks this at
/// startup before offering H.264 as a codec option in the settings catalog.
/// </summary>
public sealed class FFmpegH264EncoderFactory : IVideoEncoderFactory
{
    private readonly int _targetFps;
    private readonly long _targetBitrate;

    public FFmpegH264EncoderFactory(int targetFps = 30, long targetBitrate = 6_000_000)
    {
        _targetFps = targetFps;
        _targetBitrate = targetBitrate;
    }

    public VideoCodec Codec => VideoCodec.H264;

    public bool IsAvailable
    {
        get
        {
            FFmpegRuntime.EnsureInitialized();
            return FFmpegRuntime.IsAvailable;
        }
    }

    public IVideoEncoder CreateEncoder(int width, int height) =>
        new FFmpegH264Encoder(width, height, _targetFps, _targetBitrate);
}
