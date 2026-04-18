using System;
using System.Collections.Generic;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// libvpx VP8 software decoder wrapped as an <see cref="IVideoDecoder"/>.
///
/// <para>
/// IMPORTANT: SIPSorcery's <see cref="VpxVideoEncoder.DecodeVideo"/> ignores
/// the <see cref="VideoPixelFormatsEnum"/> argument and returns a packed
/// 24-bit BGR buffer — <c>sample.Sample.Length == width * height * 3</c>. An
/// earlier receiver tried to interpret the same bytes as I420 and produced
/// the "video cut into pieces stacked on top of each other" visual; we
/// normalize to BGRA here by padding an alpha byte per pixel so callers
/// cannot regress into that misinterpretation again.
/// </para>
/// </summary>
internal sealed class VpxDecoder : IVideoDecoder
{
    private readonly VpxVideoEncoder _decoder;
    private byte[] _bgraBuffer = Array.Empty<byte>();
    private bool _disposed;

    public VpxDecoder()
    {
        _decoder = new VpxVideoEncoder();
    }

    public VideoCodec Codec => VideoCodec.Vp8;

    public IReadOnlyList<DecodedVideoFrame> Decode(byte[] encodedSample, TimeSpan inputTimestamp)
    {
        if (_disposed || encodedSample is null || encodedSample.Length == 0)
        {
            return Array.Empty<DecodedVideoFrame>();
        }

        IEnumerable<VideoSample>? samples;
        try
        {
            samples = _decoder.DecodeVideo(encodedSample, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8);
        }
        catch (Exception)
        {
            return Array.Empty<DecodedVideoFrame>();
        }
        if (samples is null) return Array.Empty<DecodedVideoFrame>();

        var results = new List<DecodedVideoFrame>();
        foreach (var sample in samples)
        {
            if (sample.Sample is null || sample.Sample.Length == 0) continue;

            var width = (int)sample.Width;
            var height = (int)sample.Height;
            if (width <= 0 || height <= 0) continue;

            var expectedBgr = width * height * 3;
            if (sample.Sample.Length < expectedBgr) continue;

            var bgraSize = width * height * 4;
            var bgra = new byte[bgraSize];
            var src = sample.Sample;
            for (var y = 0; y < height; y++)
            {
                var srcRow = y * width * 3;
                var dstRow = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var s = srcRow + x * 3;
                    var d = dstRow + x * 4;
                    bgra[d + 0] = src[s + 0]; // B
                    bgra[d + 1] = src[s + 1]; // G
                    bgra[d + 2] = src[s + 2]; // R
                    bgra[d + 3] = 0xFF;
                }
            }

            // VP8 is sync and 1-in/1-out, so every decoded frame corresponds
            // exactly to the encodedSample we just submitted. Echo the
            // caller's content timestamp verbatim.
            results.Add(new DecodedVideoFrame(bgra, width, height, inputTimestamp));
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _decoder.Dispose(); } catch { }
    }
}
