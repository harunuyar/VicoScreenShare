using System;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace ScreenSharing.Client.Media.Codecs;

/// <summary>
/// libvpx VP8 software encoder wrapped as an <see cref="IVideoEncoder"/>.
/// Pure CPU — no hardware acceleration. Kept as a universal fallback / golden
/// reference so we can ship a working stream even on machines where hardware
/// codecs fail to initialize.
/// </summary>
internal sealed class VpxEncoder : IVideoEncoder
{
    private readonly VpxVideoEncoder _encoder;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    public VpxEncoder(int width, int height)
    {
        _width = width;
        _height = height;
        _encoder = new VpxVideoEncoder();
    }

    public VideoCodec Codec => VideoCodec.Vp8;

    public int Width => _width;

    public int Height => _height;

    public byte[]? EncodeI420(byte[] i420)
    {
        if (_disposed) return null;
        try
        {
            return _encoder.EncodeVideo(
                _width,
                _height,
                i420,
                VideoPixelFormatsEnum.I420,
                VideoCodecsEnum.VP8);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _encoder.Dispose(); } catch { }
    }
}
