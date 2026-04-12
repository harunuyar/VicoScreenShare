using System;
using System.Threading;
using ScreenSharing.Client.Platform;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace ScreenSharing.Client.Media;

/// <summary>
/// Bridges a platform <see cref="ICaptureSource"/> to a SIPSorcery-style send path
/// by running every frame through BGRA->I420 conversion and a VP8 encoder, then
/// emitting the encoded payload via a callback. Production wiring passes
/// <c>RTCPeerConnection.SendVideo</c> as the callback; tests can pass a lambda
/// that captures the emitted samples.
///
/// The streamer is single-threaded on the capture thread: frames are encoded
/// synchronously inside <see cref="OnFrameArrived"/>. VP8 encoding a 1080p frame
/// on CPU is ~2-4 ms which stays well under a 16 ms frame budget at 60fps and
/// does not meaningfully back-pressure the capture source.
/// </summary>
public sealed class CaptureStreamer : IDisposable
{
    private readonly ICaptureSource _source;
    private readonly Action<uint, byte[]> _onEncoded;
    private readonly VpxVideoEncoder _encoder;
    private byte[] _i420Buffer = Array.Empty<byte>();
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
        if (_disposed) return;
        if (frame.Format != CaptureFramePixelFormat.Bgra8) return;

        FrameCount++;

        var required = BgraToI420.RequiredOutputSize(frame.Width, frame.Height);
        if (_i420Buffer.Length < required)
        {
            _i420Buffer = new byte[required];
        }

        BgraToI420.Convert(
            frame.Pixels,
            frame.Width,
            frame.Height,
            frame.StrideBytes,
            _i420Buffer);

        byte[]? encoded;
        try
        {
            encoded = _encoder.EncodeVideo(
                frame.Width,
                frame.Height,
                _i420Buffer,
                VideoPixelFormatsEnum.I420,
                VideoCodecsEnum.VP8);
        }
        catch (Exception)
        {
            // Encoder will surface init failures on the first frame; drop it and let
            // the next frame retry rather than tearing down the capture source.
            return;
        }

        if (encoded is null || encoded.Length == 0)
        {
            return;
        }

        EncodedFrameCount++;

        // Duration is expressed in 90 kHz RTP units. Compute from the wall-clock
        // gap between frames so the receiver's playback pacing stays honest even
        // when the capture source delivers at a variable rate.
        var duration = ComputeDurationRtpUnits(frame.Timestamp);
        _onEncoded(duration, encoded);
    }

    private uint ComputeDurationRtpUnits(TimeSpan frameTimestamp)
    {
        const int rtpClockRateHz = 90_000;
        var ticks = frameTimestamp.Ticks;
        var previous = Interlocked.Exchange(ref _lastTimestampTicks, ticks);
        if (previous == 0)
        {
            // First frame — assume a nominal 30 fps step until we have a real gap.
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
        if (_disposed) return;
        _disposed = true;
        Stop();
        _encoder.Dispose();
    }
}
