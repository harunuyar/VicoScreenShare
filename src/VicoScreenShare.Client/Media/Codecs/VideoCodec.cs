namespace VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// The wire codec a stream uses. Selected per-publisher; receivers look up the
/// matching <see cref="IVideoDecoderFactory"/> and show a "can't decode this
/// codec" warning when none is available on their machine.
/// </summary>
public enum VideoCodec
{
    Vp8 = 0,
    H264 = 1,
    Av1 = 2,
}
