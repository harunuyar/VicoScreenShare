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

    public IVideoEncoder CreateEncoder(
        int width,
        int height,
        int targetFps,
        int targetBitrate,
        int gopFrames,
        IntraRefreshOptions intraRefresh = default)
    {
        // libvpx VP8 does not expose cyclic intra-refresh through the
        // SIPSorceryMedia.Encoders wrapper, so the flag is accepted for API
        // uniformity and silently ignored. Keyframe bursts stay periodic in
        // VP8 mode; users who need burst elimination should stay on H.264
        // where the Media Foundation factory honors IntraRefreshOptions.
        _ = intraRefresh;
        return new VpxEncoder(width, height);
    }
}
