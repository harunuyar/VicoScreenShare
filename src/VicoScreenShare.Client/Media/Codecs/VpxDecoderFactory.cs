namespace VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Factory for <see cref="VpxDecoder"/>. Always available because libvpx ships
/// inside SIPSorceryMedia.Encoders.
/// </summary>
public sealed class VpxDecoderFactory : IVideoDecoderFactory
{
    public VideoCodec Codec => VideoCodec.Vp8;

    public bool IsAvailable => true;

    public IVideoDecoder CreateDecoder() => new VpxDecoder();
}
