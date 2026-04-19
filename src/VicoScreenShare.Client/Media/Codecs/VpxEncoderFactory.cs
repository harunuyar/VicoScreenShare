namespace VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Factory for <see cref="VpxEncoder"/>. libvpx ships inside
/// SIPSorceryMedia.Encoders so it is always available — <see cref="IsAvailable"/>
/// is hard-coded true.
/// </summary>
public sealed class VpxEncoderFactory : IVideoEncoderFactory
{
    public VideoCodec Codec => VideoCodec.Vp8;

    public bool IsAvailable => true;

    public bool SupportsTextureInput => false;

    public IVideoEncoder CreateEncoder(int width, int height, int targetFps, int targetBitrate, int gopFrames) => new VpxEncoder(width, height);
}
