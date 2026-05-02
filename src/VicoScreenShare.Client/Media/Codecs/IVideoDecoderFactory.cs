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

    /// <summary>
    /// Construct a decoder with a known input resolution hint. Decoders
    /// whose underlying API requires a frame size at init time
    /// (notably the Microsoft AV1 MFT, which raises STREAM_CHANGE on the
    /// first sample whose Sequence Header dimensions differ from the
    /// initial output type) use this to avoid the post-init STREAM_CHANGE
    /// roundtrip that drops the first IDR and wastes ~1 s waiting for
    /// the next one. Decoders that don't care can fall back to the
    /// no-arg <see cref="CreateDecoder"/>; the default implementation
    /// here does exactly that.
    /// </summary>
    IVideoDecoder CreateDecoder(int width, int height) => CreateDecoder();
}
