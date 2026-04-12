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
/// downscale to a manageable encoder resolution (keeping aspect ratio), convert
/// BGRA to I420 via our own <see cref="BgraToI420"/> (SIPSorcery's
/// EncodeVideo(Bgra) path mislabels channels), run the I420 bytes through a
/// VP8 encoder, emit the encoded payload via an <see cref="Action{T1, T2}"/>
/// callback. Production wiring passes <c>RTCPeerConnection.SendVideo</c>;
/// tests can pass a lambda.
///
/// Downscaling is there because keyframes at modern capture sizes (4K monitors,
/// 2560x1392 windows) become hundreds of kilobytes each, which chops into
/// dozens of RTP fragments and stresses SIPSorcery's RTP reassembler enough to
/// produce visibly corrupted preview frames on the viewer side. Capping the
/// encoder input at <see cref="MaxEncoderWidth"/> x <see cref="MaxEncoderHeight"/>
/// keeps each frame small enough that fragmentation stays simple.
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
    public const int MaxEncoderWidth = 1280;
    public const int MaxEncoderHeight = 720;

    private readonly ICaptureSource _source;
    private readonly Action<uint, byte[]> _onEncoded;
    private readonly object _encodeLock = new();

    private VpxVideoEncoder _encoder;
    private int _encoderWidth;
    private int _encoderHeight;

    private byte[] _sourceBgraBuffer = Array.Empty<byte>();
    private byte[] _scaledBgraBuffer = Array.Empty<byte>();
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

    private int _diagnosticFrames;

    private void OnFrameArrived(in CaptureFrameData frame)
    {
        if (frame.Format != CaptureFramePixelFormat.Bgra8) return;

        // Source crop: even dimensions (libvpx chroma subsampling) and non-negative.
        var sourceWidth = frame.Width & ~1;
        var sourceHeight = frame.Height & ~1;
        if (sourceWidth <= 0 || sourceHeight <= 0) return;

        // Decide the encoder target: aspect-preserving fit into the max box.
        var (encWidth, encHeight) = BgraDownscale.FitWithin(
            sourceWidth, sourceHeight, MaxEncoderWidth, MaxEncoderHeight);

        // Compact the source into a packed BGRA buffer first (stride may be padded
        // by the capture backend; we always pack to width*4 for downstream math).
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
