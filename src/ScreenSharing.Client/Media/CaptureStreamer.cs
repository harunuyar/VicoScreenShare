using System;
using System.Diagnostics;
using System.Threading;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Platform;

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
    private readonly IVideoEncoderFactory _encoderFactory;
    private readonly object _encodeLock = new();

    private readonly int _maxEncoderWidth;
    private readonly int _maxEncoderHeight;
    private readonly int _targetFps;
    private readonly int _targetBitrate;
    private readonly long _minFrameGapTicks;

    private IVideoEncoder? _encoder;
    private int _encoderWidth;
    private int _encoderHeight;

    private byte[] _sourceBgraBuffer = Array.Empty<byte>();
    private byte[] _scaledBgraBuffer = Array.Empty<byte>();
    private long _lastTimestampTicks;
    private long _lastAcceptedFrameTicks = long.MinValue;
    private int _timingLogFrames;
    private bool _attached;
    private bool _disposed;

    public CaptureStreamer(ICaptureSource source, Action<uint, byte[]> onEncoded, VideoSettings settings)
        : this(source, onEncoded, settings, new VpxEncoderFactory())
    {
    }

    public CaptureStreamer(
        ICaptureSource source,
        Action<uint, byte[]> onEncoded,
        VideoSettings settings,
        IVideoEncoderFactory encoderFactory)
    {
        _source = source;
        _onEncoded = onEncoded;
        _encoderFactory = encoderFactory;
        _maxEncoderWidth = Math.Max(2, settings.MaxEncoderWidth);
        _maxEncoderHeight = Math.Max(2, settings.MaxEncoderHeight);
        _targetFps = Math.Clamp(settings.TargetFrameRate, 1, 120);
        _targetBitrate = Math.Max(500_000, settings.TargetBitrate);
        var fps = _targetFps;
        _minFrameGapTicks = TimeSpan.FromSeconds(1.0 / fps).Ticks;
    }

    /// <summary>Total frames that reached the encoder since <see cref="Start"/>.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Total frames whose encoder output was non-empty and forwarded to the callback.</summary>
    public long EncodedFrameCount { get; private set; }

    /// <summary>Total encoded bytes (cumulative). A stats poll computes
    /// bitrate as the delta between two reads divided by the elapsed wall
    /// time, so we don't have to keep a sliding window here.</summary>
    public long EncodedByteCount { get; private set; }

    /// <summary>Source frame dimensions as they come out of the capture
    /// provider, before the aspect-preserving downscale to the encoder cap.
    /// Exposed for the stats overlay.</summary>
    public int SourceWidth { get; private set; }

    /// <summary>Same as <see cref="SourceWidth"/> on the Y axis.</summary>
    public int SourceHeight { get; private set; }

    /// <summary>Current encoder target width after FitWithin. 0 before the
    /// first frame has flowed through.</summary>
    public int EncoderWidth => _encoderWidth;

    /// <summary>Current encoder target height after FitWithin.</summary>
    public int EncoderHeight => _encoderHeight;

    /// <summary>Configured fps cap read at construction time.</summary>
    public int TargetFps => _targetFps;

    /// <summary>Configured target bitrate read at construction time.</summary>
    public int TargetBitrate => _targetBitrate;

    /// <summary>Underlying codec identifier of the last encoder instance
    /// (useful for the stats panel to show "H264" vs "VP8").</summary>
    public VideoCodec? CurrentCodec => _encoder?.Codec;

    internal int CurrentEncoderWidth => _encoderWidth;

    internal int CurrentEncoderHeight => _encoderHeight;

    public void Start()
    {
        if (_attached || _disposed) return;
        _attached = true;
        _timingLogFrames = 0;
        _diagnosticFrames = 0;
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

        var timingStart = _timingLogFrames < 10 ? Stopwatch.GetTimestamp() : 0L;

        // Source crop: even dimensions (libvpx chroma subsampling) and non-negative.
        SourceWidth = frame.Width;
        SourceHeight = frame.Height;
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

        var compactEnd = timingStart != 0 ? Stopwatch.GetTimestamp() : 0L;

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

        var downscaleEnd = timingStart != 0 ? Stopwatch.GetTimestamp() : 0L;

        if (_diagnosticFrames < 3)
        {
            Interlocked.Increment(ref _diagnosticFrames);
            DebugLog.Write(
                $"[send] src w={frame.Width} h={frame.Height} stride={frame.StrideBytes} -> enc w={encWidth} h={encHeight}");
        }

        var timestamp = frame.Timestamp;
        EncodeAndEmit(encoderInput, encoderInputStride, encWidth, encHeight, timestamp);

        if (timingStart != 0)
        {
            var encodeEnd = Stopwatch.GetTimestamp();
            _timingLogFrames++;
            var ticksPerMs = Stopwatch.Frequency / 1000.0;
            var compactMs = (compactEnd - timingStart) / ticksPerMs;
            var downscaleMs = (downscaleEnd - compactEnd) / ticksPerMs;
            var encodeMs = (encodeEnd - downscaleEnd) / ticksPerMs;
            var totalMs = (encodeEnd - timingStart) / ticksPerMs;
            DebugLog.Write(
                $"[timing #{_timingLogFrames}] compact={compactMs:F1}ms downscale={downscaleMs:F1}ms encode={encodeMs:F1}ms total={totalMs:F1}ms");
        }
    }

    private void EncodeAndEmit(byte[] encoderInput, int encoderStrideBytes, int width, int height, TimeSpan timestamp)
    {
        byte[]? encoded;
        lock (_encodeLock)
        {
            if (_disposed) return;

            FrameCount++;

            // Rebuild the encoder if the stream dimensions changed. Both libvpx
            // and Media Foundation lock their encoder state to the first
            // frame's dimensions and cannot retarget on the fly, so the
            // abstraction contract is: one encoder instance per (width,height)
            // tuple and the caller swaps them out on change.
            if (_encoder is null || width != _encoderWidth || height != _encoderHeight)
            {
                try { _encoder?.Dispose(); } catch { }
                _encoder = _encoderFactory.CreateEncoder(width, height, _targetFps, _targetBitrate);
                _encoderWidth = width;
                _encoderHeight = height;
            }

            // BGRA -> encoder's native pixel format (NV12 for H.264 / I420 for
            // VP8) happens inside the encoder now — a single fused pass
            // instead of the double conversion (BGRA -> I420 -> NV12) this
            // hot path used to do on the capture thread.
            encoded = _encoder.EncodeBgra(encoderInput, encoderStrideBytes);

            if (encoded is null || encoded.Length == 0)
            {
                return;
            }

            EncodedFrameCount++;
            EncodedByteCount += encoded.Length;
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
            try { _encoder?.Dispose(); } catch { }
            _encoder = null;
        }
    }
}
