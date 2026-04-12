using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenSharing.Client.Media.Codecs;

/// <summary>
/// Holds the set of video codec implementations the current host supports.
/// The Desktop startup builds this once and hands it to the Avalonia app via
/// <c>App.VideoCodecCatalog</c>; <see cref="RoomViewModel"/> then looks up the
/// factory pair for whichever codec the user picked in settings and passes
/// them into the <see cref="CaptureStreamer"/> / <see cref="StreamReceiver"/>
/// / <see cref="Services.WebRtcSession"/> pipeline at share time.
///
/// VP8 is always registered because libvpx ships inside SIPSorceryMedia.
/// Additional codecs (H.264 via FFmpeg, future AV1) are registered when the
/// host can construct them; missing codecs simply don't appear in
/// <see cref="AvailableCodecs"/>.
/// </summary>
public sealed class VideoCodecCatalog
{
    private readonly Dictionary<VideoCodec, IVideoEncoderFactory> _encoders = new();
    private readonly Dictionary<VideoCodec, IVideoDecoderFactory> _decoders = new();

    public VideoCodecCatalog()
    {
        // VP8 is the universal baseline: every client can encode and decode it.
        Register(new VpxEncoderFactory(), new VpxDecoderFactory());
    }

    public void Register(IVideoEncoderFactory encoderFactory, IVideoDecoderFactory decoderFactory)
    {
        if (encoderFactory.Codec != decoderFactory.Codec)
        {
            throw new ArgumentException(
                $"encoder and decoder factories must cover the same codec; got {encoderFactory.Codec} vs {decoderFactory.Codec}");
        }
        _encoders[encoderFactory.Codec] = encoderFactory;
        _decoders[decoderFactory.Codec] = decoderFactory;
    }

    public IReadOnlyList<VideoCodec> AvailableCodecs =>
        _encoders
            .Where(kv => kv.Value.IsAvailable && _decoders.TryGetValue(kv.Key, out var dec) && dec.IsAvailable)
            .Select(kv => kv.Key)
            .ToArray();

    public bool TryGet(VideoCodec codec, out IVideoEncoderFactory encoderFactory, out IVideoDecoderFactory decoderFactory)
    {
        if (_encoders.TryGetValue(codec, out var enc) && enc.IsAvailable &&
            _decoders.TryGetValue(codec, out var dec) && dec.IsAvailable)
        {
            encoderFactory = enc;
            decoderFactory = dec;
            return true;
        }
        encoderFactory = null!;
        decoderFactory = null!;
        return false;
    }

    /// <summary>
    /// Resolve the codec the caller asked for, falling back to VP8 (which is
    /// always available) when the requested codec is not registered or its
    /// factory reports unavailable. Returns the actually-selected codec so the
    /// caller can surface a "fell back from X to Vp8" warning.
    /// </summary>
    public (VideoCodec selected, IVideoEncoderFactory encoderFactory, IVideoDecoderFactory decoderFactory)
        ResolveOrFallback(VideoCodec preferred)
    {
        if (TryGet(preferred, out var enc, out var dec))
        {
            return (preferred, enc, dec);
        }
        TryGet(VideoCodec.Vp8, out var vpxEnc, out var vpxDec);
        return (VideoCodec.Vp8, vpxEnc, vpxDec);
    }
}
