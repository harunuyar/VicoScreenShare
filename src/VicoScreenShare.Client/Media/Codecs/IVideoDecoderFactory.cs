namespace VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Builds <see cref="IVideoDecoder"/> instances for one specific codec.
/// The receiver uses <see cref="IsAvailable"/> to decide whether to warn the
/// user about an unsupported stream rather than attempting to decode and
/// producing garbled output.
/// </summary>
public interface IVideoDecoderFactory
{
    VideoCodec Codec { get; }

    bool IsAvailable { get; }

    IVideoDecoder CreateDecoder();
}
