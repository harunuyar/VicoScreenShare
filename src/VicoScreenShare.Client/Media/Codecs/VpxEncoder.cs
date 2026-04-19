namespace VicoScreenShare.Client.Media.Codecs;

using System;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

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
    private byte[] _i420Buffer = Array.Empty<byte>();
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

    public bool SupportsTextureInput => false;

    public EncodedFrame? EncodeTexture(IntPtr nativeTexture, int sourceWidth, int sourceHeight, TimeSpan inputTimestamp) =>
        throw new NotSupportedException("libvpx VP8 is a CPU-only encoder and has no texture ingest path.");

    public EncodedFrame? EncodeBgra(byte[] bgra, int stride, TimeSpan inputTimestamp)
    {
        if (_disposed)
        {
            return null;
        }

        // libvpx wants planar I420 input, so the BGRA-to-I420 conversion
        // happens here rather than on the capture thread. Buffer is reused
        // across calls to avoid GC pressure on the hot path.
        var required = BgraToI420.RequiredOutputSize(_width, _height);
        if (_i420Buffer.Length < required)
        {
            _i420Buffer = new byte[required];
        }
        BgraToI420.Convert(bgra.AsSpan(0, _height * stride), _width, _height, stride, _i420Buffer);

        byte[]? bytes;
        try
        {
            bytes = _encoder.EncodeVideo(
                _width,
                _height,
                _i420Buffer,
                VideoPixelFormatsEnum.I420,
                VideoCodecsEnum.VP8);
        }
        catch (Exception)
        {
            return null;
        }

        // VP8 is sync — whatever bytes come back correspond exactly to the
        // input we just submitted, so the content timestamp is the caller's
        // value verbatim.
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        return new EncodedFrame(bytes, inputTimestamp);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _encoder.Dispose(); } catch { }
    }
}
