namespace ScreenSharing.Client.Media.Codecs;

/// <summary>
/// Builds <see cref="IVideoEncoder"/> instances for one specific codec.
/// <see cref="IsAvailable"/> lets the settings UI disable a codec choice at
/// startup on machines where the backing encoder cannot be constructed — e.g.
/// an H.264 Media Foundation encoder on a machine without a matching GPU, or
/// AV1 HW encode on a GPU that does not support it.
/// </summary>
public interface IVideoEncoderFactory
{
    VideoCodec Codec { get; }

    bool IsAvailable { get; }

    IVideoEncoder CreateEncoder(int width, int height);
}
