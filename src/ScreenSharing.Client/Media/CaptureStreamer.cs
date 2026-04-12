using System;
using System.Threading;
using ScreenSharing.Client.Platform;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace ScreenSharing.Client.Media;

/// <summary>
/// Bridges a platform <see cref="ICaptureSource"/> to a SIPSorcery-style send path
/// by handing BGRA frames directly to <see cref="VpxVideoEncoder.EncodeVideo"/>
/// — SIPSorcery's built-in PixelConverter takes care of BGRA -> I420 using the
/// same YUV coefficients its VP8 pipeline decodes with, so a hand-rolled
/// conversion on our side cannot drift from the codec's expectations.
///
/// The streamer is single-threaded on the capture thread: frames are encoded
/// synchronously inside <see cref="OnFrameArrived"/>. VP8 encoding a 1080p
/// frame on CPU is ~2-4 ms which stays well under a 16 ms budget at 60fps.
/// <see cref="Dispose"/> and <see cref="OnFrameArrived"/> are serialized by a
/// mutex so a stop-streaming cannot race with a mid-flight frame inside the
/// native libvpx encoder (that race crashes the CLR with
/// <c>ExecutionEngineException</c> the moment the encoder frees its state).
/// </summary>
public sealed class CaptureStreamer : IDisposable
{
    private readonly ICaptureSource _source;
    private readonly Action<uint, byte[]> _onEncoded;
    private readonly VpxVideoEncoder _encoder;
    private readonly object _encodeLock = new();
    private byte[] _bgraBuffer = Array.Empty<byte>();
    private long _lastTimestampTicks;
    private bool _attached;
    private bool _disposed;

    public CaptureStreamer(ICaptureSource source, Action<uint, byte[]> onEncoded)
    {
        _source = source;
        _onEncoded = onEncoded;
        _encoder = new VpxVideoEncoder();
    }

    /// <summary>Total frames that reached the encoder since <see cref="Start"/>.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Total frames whose encoder output was non-empty and forwarded to the callback.</summary>
    public long EncodedFrameCount { get; private set; }

    public void Start()
    {
        if (_attached || _disposed) return;
        _attached = true;
        _source.FrameArrived += OnFrameArrived;
    }

    public void Stop()
    {
        if (!_attached) return;
        _attached = false;
        _source.FrameArrived -= OnFrameArrived;
    }

    private void OnFrameArrived(in CaptureFrameData frame)
    {
        if (frame.Format != CaptureFramePixelFormat.Bgra8) return;

        // Copy out of the source-owned span into our own buffer first so we can
        // drop out of the span scope and take the encode lock (you cannot hold a
        // ref struct across a lock block). The buffer is reused across frames to
        // avoid GC pressure at 60fps.
        var byteCount = frame.Height * frame.Width * 4;
        if (_bgraBuffer.Length < byteCount)
        {
            _bgraBuffer = new byte[byteCount];
        }
        if (frame.StrideBytes == frame.Width * 4)
        {
            frame.Pixels.Slice(0, byteCount).CopyTo(_bgraBuffer);
        }
        else
        {
            for (var y = 0; y < frame.Height; y++)
            {
                frame.Pixels
                    .Slice(y * frame.StrideBytes, frame.Width * 4)
                    .CopyTo(_bgraBuffer.AsSpan(y * frame.Width * 4, frame.Width * 4));
            }
        }

        var width = frame.Width;
        var height = frame.Height;
        var timestamp = frame.Timestamp;

        EncodeAndEmit(width, height, byteCount, timestamp);
    }

    private void EncodeAndEmit(int width, int height, int byteCount, TimeSpan timestamp)
    {
        byte[]? encoded;
        lock (_encodeLock)
        {
            if (_disposed) return;

            FrameCount++;

            try
            {
                encoded = _encoder.EncodeVideo(
                    width,
                    height,
                    _bgraBuffer,
                    VideoPixelFormatsEnum.Bgra,
                    VideoCodecsEnum.VP8);
            }
            catch (Exception)
            {
                // Encoder will surface init failures on the first frame; drop it
                // and let the next frame retry instead of tearing down capture.
                return;
            }

            if (encoded is null || encoded.Length == 0)
            {
                return;
            }

            EncodedFrameCount++;
        }

        // The callback runs outside the lock so a slow RTCPeerConnection.SendVideo
        // cannot block Dispose for longer than one encode pass.
        var duration = ComputeDurationRtpUnits(timestamp);
        _onEncoded(duration, encoded);
    }

    private uint ComputeDurationRtpUnits(TimeSpan frameTimestamp)
    {
        const int rtpClockRateHz = 90_000;
        var ticks = frameTimestamp.Ticks;
        var previous = Interlocked.Exchange(ref _lastTimestampTicks, ticks);
        if (previous == 0)
        {
            // First frame -- assume a nominal 30 fps step until we have a real gap.
            return rtpClockRateHz / 30;
        }
        var elapsed = TimeSpan.FromTicks(ticks - previous);
        if (elapsed <= TimeSpan.Zero)
        {
            return rtpClockRateHz / 60;
        }
        var rtpUnits = (long)(elapsed.TotalSeconds * rtpClockRateHz);
        if (rtpUnits <= 0) rtpUnits = rtpClockRateHz / 60;
        if (rtpUnits > rtpClockRateHz) rtpUnits = rtpClockRateHz;
        return (uint)rtpUnits;
    }

    public void Dispose()
    {
        // First detach from the source so new frames stop arriving. Then acquire
        // the encode lock so any currently-executing EncodeVideo completes before
        // we free the native encoder. Without this pairing, libvpx's internal
        // state gets freed under an in-flight encode and the CLR throws
        // ExecutionEngineException from the native memory corruption.
        Stop();
        lock (_encodeLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _encoder.Dispose(); } catch { }
        }
    }
}
