using System;
using System.Threading;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Platform;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace ScreenSharing.Client.Media;

/// <summary>
/// Bridges a platform <see cref="ICaptureSource"/> to a SIPSorcery send path.
///
/// Pipeline per frame: compact the source-owned span into a packed BGRA buffer,
/// downscale to the <see cref="VideoSettings.MaxEncoderWidth"/> x
/// <see cref="VideoSettings.MaxEncoderHeight"/> cap (aspect preserved), convert
/// BGRA to I420 via our own <see cref="BgraToI420"/> (SIPSorcery's
/// EncodeVideo(Bgra) path mislabels channels), run the I420 bytes through a
/// VP8 encoder, emit the encoded payload via an <see cref="Action{T1, T2}"/>
/// callback. Production wiring passes <c>RTCPeerConnection.SendVideo</c>;
/// tests can pass a lambda.
///
/// Frame rate is throttled to <see cref="VideoSettings.TargetFrameRate"/> by
/// dropping any frame whose timestamp is closer than <c>1 / targetFps</c>
/// seconds to the previously encoded frame. This reuses the capture source's
/// own timestamps so a slower capture rate simply passes through unchanged.
///
/// The encoder is rebuilt whenever the target dimensions change. libvpx locks
/// its encoder state on first frame and silently produces garbage if you feed
/// it a differently-sized frame later.
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

    private readonly int _maxEncoderWidth;
    private readonly int _maxEncoderHeight;
    private readonly long _minFrameGapTicks;

    private VpxVideoEncoder _encoder;
    private int _encoderWidth;
    private int _encoderHeight;

    private byte[] _sourceBgraBuffer = Array.Empty<byte>();
    private byte[] _scaledBgraBuffer = Array.Empty<byte>();
    private byte[] _i420Buffer = Array.Empty<byte>();
    private long _lastTimestampTicks;
    private long _lastAcceptedFrameTicks = long.MinValue;
    private bool _attached;
    private bool _disposed;

    public CaptureStreamer(ICaptureSource source, Action<uint, byte[]> onEncoded, VideoSettings settings)
    {
        _source = source;
        _onEncoded = onEncoded;
        _maxEncoderWidth = Math.Max(2, settings.MaxEncoderWidth);
        _maxEncoderHeight = Math.Max(2, settings.MaxEncoderHeight);
        var fps = Math.Clamp(settings.TargetFrameRate, 1, 120);
        _minFrameGapTicks = TimeSpan.FromSeconds(1.0 / fps).Ticks;
        _encoder = new VpxVideoEncoder();
    }

    /// <summary>Total frames that reached the encoder since <see cref="Start"/>.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Total frames whose encoder output was non-empty and forwarded to the callback.</summary>
    public long EncodedFrameCount { get; private set; }

    internal int CurrentEncoderWidth => _encoderWidth;

    internal int CurrentEncoderHeight => _encoderHeight;

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

    private int _diagnosticFrames;

    private void OnFrameArrived(in CaptureFrameData frame)
    {
        if (frame.Format != CaptureFramePixelFormat.Bgra8) return;

        // FPS throttle: drop a frame if its timestamp is less than
        // _minFrameGapTicks after the previously accepted frame. First frame
        // (_lastAcceptedFrameTicks == long.MinValue) always passes.
        var ts = frame.Timestamp.Ticks;
        if (_lastAcceptedFrameTicks != long.MinValue &&
            ts - _lastAcceptedFrameTicks < _minFrameGapTicks)
        {
            return;
        }
        _lastAcceptedFrameTicks = ts;

        // Source crop: even dimensions (libvpx chroma subsampling) and non-negative.
        var sourceWidth = frame.Width & ~1;
        var sourceHeight = frame.Height & ~1;
        if (sourceWidth <= 0 || sourceHeight <= 0) return;

        // Decide the encoder target: aspect-preserving fit into the max box.
        var (encWidth, encHeight) = BgraDownscale.FitWithin(
            sourceWidth, sourceHeight, _maxEncoderWidth, _maxEncoderHeight);

        // Compact the source into a packed BGRA buffer first.
        var packedRowBytes = sourceWidth * 4;
        var packedBytes = packedRowBytes * sourceHeight;
        if (_sourceBgraBuffer.Length < packedBytes)
        {
            _sourceBgraBuffer = new byte[packedBytes];
        }
        for (var y = 0; y < sourceHeight; y++)
        {
            frame.Pixels
                .Slice(y * frame.StrideBytes, packedRowBytes)
                .CopyTo(_sourceBgraBuffer.AsSpan(y * packedRowBytes, packedRowBytes));
        }

        // Downscale (if needed) into the scaled buffer.
        byte[] encoderInput;
        int encoderInputStride;
        if (encWidth == sourceWidth && encHeight == sourceHeight)
        {
            encoderInput = _sourceBgraBuffer;
            encoderInputStride = packedRowBytes;
        }
        else
        {
            var scaledBytes = encWidth * encHeight * 4;
            if (_scaledBgraBuffer.Length < scaledBytes)
            {
                _scaledBgraBuffer = new byte[scaledBytes];
            }
            BgraDownscale.Downscale(
                _sourceBgraBuffer.AsSpan(0, packedBytes),
                sourceWidth,
                sourceHeight,
                _scaledBgraBuffer.AsSpan(0, scaledBytes),
                encWidth,
                encHeight);
            encoderInput = _scaledBgraBuffer;
            encoderInputStride = encWidth * 4;
        }

        if (_diagnosticFrames < 3)
        {
            Interlocked.Increment(ref _diagnosticFrames);
            DebugLog.Write(
                $"[send] src w={frame.Width} h={frame.Height} stride={frame.StrideBytes} -> enc w={encWidth} h={encHeight}");
        }

        var timestamp = frame.Timestamp;
        EncodeAndEmit(encoderInput, encoderInputStride, encWidth, encHeight, timestamp);
    }

    private void EncodeAndEmit(byte[] encoderInput, int encoderStrideBytes, int width, int height, TimeSpan timestamp)
    {
        byte[]? encoded;
        lock (_encodeLock)
        {
            if (_disposed) return;

            FrameCount++;

            // Rebuild the encoder if the stream dimensions changed. libvpx
            // initializes its encoder state from the first frame's dimensions
            // and cannot retarget on the fly.
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
                encoderInput.AsSpan(0, height * encoderStrideBytes),
                width,
                height,
                encoderStrideBytes,
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
                return;
            }

            if (encoded is null || encoded.Length == 0)
            {
                return;
            }

            EncodedFrameCount++;
        }

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
        Stop();
        lock (_encodeLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _encoder.Dispose(); } catch { }
        }
    }
}
