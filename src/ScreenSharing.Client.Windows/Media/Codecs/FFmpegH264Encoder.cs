using System;
using FFmpeg.AutoGen;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using IVideoEncoder = ScreenSharing.Client.Media.Codecs.IVideoEncoder;

namespace ScreenSharing.Client.Windows.Media.Codecs;

/// <summary>
/// H.264 encoder backed by SIPSorceryMedia.FFmpeg. Delegates the actual
/// encode through <see cref="FFmpegVideoEncoder.EncodeVideo"/> with
/// <see cref="VideoPixelFormatsEnum.I420"/> input — <see cref="CaptureStreamer"/>
/// already produces I420 bytes via <see cref="BgraToI420"/>, so no extra
/// conversion pass is needed.
///
/// On construction we try to pick a hardware codec name (h264_nvenc, h264_qsv,
/// h264_amf) before falling back to the FFmpeg default (libx264 software).
/// Each <see cref="FFmpegVideoEncoder.SetCodec(AVCodecID, string, System.Collections.Generic.Dictionary{string,string})"/>
/// returns a bool so we can probe in order.
/// </summary>
internal sealed class FFmpegH264Encoder : IVideoEncoder
{
    private readonly FFmpegVideoEncoder _encoder;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    public FFmpegH264Encoder(int width, int height, int targetFps, long targetBitrate)
    {
        _width = width;
        _height = height;
        _encoder = new FFmpegVideoEncoder();

        // Try hardware encoders in order of availability on typical Windows
        // gaming machines. Each SetCodec returns false if FFmpeg can't find or
        // initialize that codec, so we walk the list until one sticks — or
        // leave the encoder in its default state (libx264) if nothing does.
        string[] hwCandidates = { "h264_nvenc", "h264_qsv", "h264_amf" };
        var picked = "libx264 (software fallback)";
        foreach (var name in hwCandidates)
        {
            if (_encoder.SetCodec(AVCodecID.AV_CODEC_ID_H264, name))
            {
                picked = name;
                break;
            }
        }

        // Bitrate shape: we set the average to the target and let min/max
        // cluster around it. FFmpeg's rate controller does the smoothing.
        // Tolerance is in bits per second, per the SIPSorcery signature.
        var halfRange = Math.Max(targetBitrate / 2, 500_000);
        _encoder.SetBitrate(
            avgBitrate: targetBitrate,
            toleranceBitrate: (int)Math.Min(halfRange, int.MaxValue),
            minBitrate: Math.Max(targetBitrate - halfRange, 500_000),
            maxBitrate: targetBitrate + halfRange);

        _encoder.InitialiseEncoder(AVCodecID.AV_CODEC_ID_H264, width, height, targetFps);
        DebugLog.Write($"[ffmpeg] h264 encoder {picked} {width}x{height}@{targetFps} {targetBitrate} bps");
    }

    public VideoCodec Codec => VideoCodec.H264;

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
                VideoCodecsEnum.H264);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[ffmpeg] h264 encode threw: {ex.Message}");
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
