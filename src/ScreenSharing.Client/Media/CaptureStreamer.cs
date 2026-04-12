using System;
using System.Threading;
using ScreenSharing.Client.Platform;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace ScreenSharing.Client.Media;

/// <summary>
/// Bridges a platform <see cref="ICaptureSource"/> to a SIPSorcery-style send path.
///
/// Each frame: crop to even dimensions, compact into a packed BGRA buffer, convert
/// to I420 via our own <see cref="BgraToI420"/> (SIPSorcery's EncodeVideo(Bgra...)
/// path mislabels channels — verified by a VP8 round-trip unit test), feed the
/// I420 bytes to a VP8 encoder, emit the encoded payload through a callback.
/// Production wiring passes <c>RTCPeerConnection.SendVideo</c> as the callback;
/// tests can pass a lambda.
///
/// The encoder is rebuilt whenever the captured frame dimensions change because
/// libvpx only configures itself on construction and silently produces garbage
/// (striped, misaligned preview) when fed a differently-sized frame later.
///
/// <see cref="Dispose"/> and <see cref="OnFrameArrived"/> are serialized by a
/// mutex so a stop-streaming cannot race with a mid-flight frame inside the
/// native libvpx encoder — that race crashes the CLR with
/// <c>ExecutionEngineException</c> the moment the encoder frees its state.
/// </summary>
public sealed class CaptureStreamer : IDisposable
{
    private readonly ICaptureSource _source;
    private readonly Action<uint, byte[]> _onEncoded;
    private readonly object _encodeLock = new();

    private VpxVideoEncoder _encoder;
    private int _encoderWidth;
    private int _encoderHeight;

    private byte[] _bgraBuffer = Array.Empty<byte>();
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
        if (frame.Format != CaptureFramePixelFormat.Bgra8) return;

        // libvpx's VP8 encoder wants even dimensions (I420 uses 2x2 chroma
        // subsampling blocks). Round down each axis to the nearest even value;
        // worst case we drop a single row or column off a window edge.
        var width = frame.Width & ~1;
        var height = frame.Height & ~1;
        if (width <= 0 || height <= 0) return;

        var byteCount = height * width * 4;
        if (_bgraBuffer.Length < byteCount)
        {
            _bgraBuffer = new byte[byteCount];
        }

        // Compact the cropped rectangle from the source-owned span (which may
        // have arbitrary stride padding) into a packed width*4 buffer. Doing
        // this outside the lock lets us drop out of the ref-struct span scope
        // before we try to acquire _encodeLock.
        for (var y = 0; y < height; y++)
        {
            frame.Pixels
                .Slice(y * frame.StrideBytes, width * 4)
                .CopyTo(_bgraBuffer.AsSpan(y * width * 4, width * 4));
        }

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

            // Rebuild the encoder if the stream dimensions changed. libvpx
            // initializes its encoder state from the first frame's dimensions
            // and cannot retarget on the fly. Feeding it a differently-sized
            // buffer after init produces a striped / shifted output frame.
            if (width != _encoderWidth || height != _encoderHeight)
            {
                try { _encoder.Dispose(); } catch { }
                _encoder = new VpxVideoEncoder();
                _encoderWidth = width;
                _encoderHeight = height;
            }

            var i420Required = BgraToI420.RequiredOutputSize(width, height);
            if (_i420Buffer.Length < i420Required)
            {
                _i420Buffer = new byte[i420Required];
            }
            BgraToI420.Convert(
                _bgraBuffer.AsSpan(0, byteCount),
                width,
                height,
                bgraStrideBytes: width * 4,
                _i420Buffer);

            try
            {
                encoded = _encoder.EncodeVideo(
                    width,
                    height,
                    _i420Buffer,
                    VideoPixelFormatsEnum.I420,
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
