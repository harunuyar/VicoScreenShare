using System;
using System.Collections.Generic;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace ScreenSharing.Client.Windows.Media.Codecs;

/// <summary>
/// H.264 decoder backed by <see cref="FFmpegVideoEncoder.DecodeVideo"/>. The
/// FFmpeg decoder returns a packed 24-bit buffer regardless of the pixel
/// format argument (same quirk as the VP8 path), so we expand it to packed
/// BGRA here for the renderer.
///
/// NOTE: at the time of writing I have not verified whether FFmpeg's decoder
/// returns the bytes in BGR or RGB order. If colors show up swapped after
/// the first H.264 roundtrip, flip the <c>bgra[d+0]</c> / <c>bgra[d+2]</c>
/// assignments below.
/// </summary>
internal sealed class FFmpegH264Decoder : IVideoDecoder
{
    private readonly FFmpegVideoEncoder _decoder;
    private bool _disposed;

    public FFmpegH264Decoder()
    {
        _decoder = new FFmpegVideoEncoder();
    }

    public VideoCodec Codec => VideoCodec.H264;

    public IReadOnlyList<DecodedVideoFrame> Decode(byte[] encodedSample)
    {
        if (_disposed || encodedSample is null || encodedSample.Length == 0)
        {
            return Array.Empty<DecodedVideoFrame>();
        }

        IEnumerable<VideoSample>? samples;
        try
        {
            samples = _decoder.DecodeVideo(encodedSample, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[ffmpeg] h264 decode threw: {ex.Message}");
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

            var expectedPacked = width * height * 3;
            if (sample.Sample.Length < expectedPacked) continue;

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
                    bgra[d + 0] = src[s + 0];
                    bgra[d + 1] = src[s + 1];
                    bgra[d + 2] = src[s + 2];
                    bgra[d + 3] = 0xFF;
                }
            }

            results.Add(new DecodedVideoFrame(bgra, width, height));
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
