namespace VicoScreenShare.Client.Media;

using System;
using System.Diagnostics;
using System.Threading;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;

/// <summary>
/// Bridges a platform <see cref="ICaptureSource"/> to a SIPSorcery send path.
///
/// Pipeline per frame: compact the source-owned span into a packed BGRA buffer,
/// downscale to <see cref="VideoSettings.TargetHeight"/> (width is derived
/// from the source aspect ratio so output never distorts), convert
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
    private readonly Action<uint, byte[], TimeSpan> _onEncoded;
    private readonly IVideoEncoderFactory _encoderFactory;
    private readonly object _encodeLock = new();

    private readonly int _targetHeight;
    private readonly int _targetFps;
    private readonly int _targetBitrate;
    private readonly int _gopFrames;
    private readonly bool _requiresMacroblockAlignedDimensions;
    private readonly long _frameGapTicks;
    private readonly long _frameGapToleranceTicks;
    private long _nextDeadlineTicks;

    private IVideoEncoder? _encoder;
    private IAsyncEncodedOutputSource? _asyncOutput;
    private int _encoderWidth;
    private int _encoderHeight;

    private byte[] _sourceBgraBuffer = Array.Empty<byte>();
    private byte[] _scaledBgraBuffer = Array.Empty<byte>();
    private long _lastTimestampTicks;
    private long _lastAcceptedFrameTicks = long.MinValue;
    private long _lastArrivalWallTicks;
    private long _lastArrivalFrameTicks;
    private int _lastLoggedSourceWidth;
    private int _lastLoggedSourceHeight;
    private long _startWallTicks;
    private bool _usingTexturePath;
    private bool _attached;
    private bool _disposed;

    /// <summary>
    /// Threshold in milliseconds for the [arrival-*-spike] log: an arrival
    /// gap larger than this signals the capture provider stalled (DWM
    /// throttling, GPU contention, encoder backpressure on the framepool).
    /// 2 frame periods at the configured fps — at 60 fps that's 33 ms,
    /// at 30 fps it's 67 ms, at 120 fps it's 17 ms. Hardcoding 33 ms (as
    /// I did initially) means every gap fires at 30 fps and real spikes
    /// stay invisible at 120 fps; the threshold has to scale with fps.
    /// </summary>
    private double ArrivalSpikeThresholdMs => 2000.0 / _targetFps;

    /// <summary>
    /// Threshold in milliseconds for the [timing-*-spike] log: a
    /// synchronous encode that ate half the per-frame budget is worth
    /// surfacing. Half a frame period at the configured fps — at 60 fps
    /// that's ~8 ms, at 30 fps ~17 ms, at 120 fps ~4 ms. Async encoders
    /// (NVENC) usually return ~immediately on the calling thread so they
    /// never spike on this metric; the spike is meaningful for the sync
    /// MFT path or pipeline contention.
    /// </summary>
    private double EncodeSpikeThresholdMs => 500.0 / _targetFps;

    public CaptureStreamer(ICaptureSource source, Action<uint, byte[], TimeSpan> onEncoded, VideoSettings settings)
        : this(source, onEncoded, settings, new VpxEncoderFactory())
    {
    }

    public CaptureStreamer(
        ICaptureSource source,
        Action<uint, byte[], TimeSpan> onEncoded,
        VideoSettings settings,
        IVideoEncoderFactory encoderFactory)
    {
        _source = source;
        _onEncoded = onEncoded;
        _encoderFactory = encoderFactory;
        _requiresMacroblockAlignedDimensions = encoderFactory is IVideoEncoderDimensionPolicy { RequiresMacroblockAlignedDimensions: true };
        // 0 means "match source" — let the first frame decide.
        _targetHeight = settings.TargetHeight <= 0 ? 0 : Math.Max(2, settings.TargetHeight);
        _targetFps = Math.Clamp(settings.TargetFrameRate, 1, 240);
        _targetBitrate = Math.Max(500_000, settings.TargetBitrate);
        // GOP frames = keyframeIntervalSeconds * fps, rounded. Default 2s
        // @ 60fps = 120. Controls how often the encoder emits an IDR so a
        // mid-stream viewer has a decodable starting point.
        var keyframeSec = settings.KeyframeIntervalSeconds <= 0 ? 2.0 : settings.KeyframeIntervalSeconds;
        _gopFrames = Math.Max(1, (int)Math.Round(keyframeSec * _targetFps));
        var fps = _targetFps;
        // Phase-locked throttle: we keep a monotonic deadline and accept any
        // frame whose timestamp (+ a small tolerance for jitter) reaches the
        // deadline. Each accept advances the deadline by exactly one frame
        // period, so WGC delivery jitter can't snowball into dropped frames
        // the way a strict "gap >= period" check does. Tolerance is 20% of
        // the period — generous enough that arrivals within a frame-time
        // jitter window still get through.
        _frameGapTicks = TimeSpan.FromSeconds(1.0 / fps).Ticks;
        _frameGapToleranceTicks = _frameGapTicks / 5;
    }

    /// <summary>Total frames that reached the encoder since <see cref="Start"/>.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Total frames whose encoder output was non-empty and forwarded to the callback.</summary>
    public long EncodedFrameCount { get; private set; }

    /// <summary>Total encoded bytes (cumulative). A stats poll computes
    /// bitrate as the delta between two reads divided by the elapsed wall
    /// time, so we don't have to keep a sliding window here.</summary>
    public long EncodedByteCount { get; private set; }

    private void NoteEncodedFrame(int bytes)
    {
        EncodedFrameCount++;
        EncodedByteCount += bytes;
    }

    /// <summary>Source frame dimensions as they come out of the capture
    /// provider, before the aspect-preserving downscale. Exposed for the
    /// stats overlay.</summary>
    public int SourceWidth { get; private set; }

    /// <summary>Same as <see cref="SourceWidth"/> on the Y axis.</summary>
    public int SourceHeight { get; private set; }

    /// <summary>Current encoder target width, derived from the source aspect.
    /// 0 before the first frame has flowed through.</summary>
    public int EncoderWidth => _encoderWidth;

    /// <summary>Current encoder target height.</summary>
    public int EncoderHeight => _encoderHeight;

    /// <summary>Configured fps cap read at construction time.</summary>
    public int TargetFps => _targetFps;

    /// <summary>Configured target bitrate read at construction time.</summary>
    public int TargetBitrate => _targetBitrate;

    /// <summary>
    /// Reconfigure the underlying encoder's target bitrate at runtime.
    /// Called by the adaptive-bitrate coordinator when RTCP Receiver Reports
    /// indicate sustained upstream loss. Safe to call before the first
    /// encode too — will apply when the encoder is constructed.
    /// </summary>
    public void UpdateBitrate(int bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return;
        }
        try
        {
            _encoder?.UpdateBitrate(bitsPerSecond);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[send] UpdateBitrate({bitsPerSecond / 1000} kbps) threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Underlying codec identifier of the last encoder instance
    /// (useful for the stats panel to show "H264" vs "VP8").</summary>
    public VideoCodec? CurrentCodec => _encoder?.Codec;

    internal int CurrentEncoderWidth => _encoderWidth;

    internal int CurrentEncoderHeight => _encoderHeight;

    public void Start()
    {
        if (_attached || _disposed)
        {
            return;
        }

        _attached = true;
        _lastLoggedSourceWidth = 0;
        _lastLoggedSourceHeight = 0;
        _startWallTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        // Share-start spec line. ReplaceEncoder will log the resolved
        // encoder backend + dimensions when the first frame arrives;
        // this line records the publisher's requested config (codec
        // family is decided by the caller-supplied encoderFactory).
        DebugLog.Write($"[settings-share] requested fps={_targetFps} bitrate={_targetBitrate / 1000}kbps gop={_gopFrames}f targetHeight={(_targetHeight == 0 ? "source" : _targetHeight.ToString())} alignedDims={_requiresMacroblockAlignedDimensions}");

        // When the encoder factory advertises texture input AND the capture
        // source can produce textures (WindowsCaptureSource fires the event,
        // fakes used by unit tests don't), take the GPU path — D3D11 Video
        // Processor does the scale, NVENC does BGRA->NV12 internally, and
        // there is no CPU readback on the hot path. Otherwise fall through
        // to the BGRA compact + downscale + encode path.
        if (_encoderFactory.SupportsTextureInput)
        {
            _source.TextureArrived += OnTextureArrived;
            _usingTexturePath = true;
            DebugLog.Write("[send] using GPU texture path (zero CPU readback)");
        }
        else
        {
            _source.FrameArrived += OnFrameArrived;
            _usingTexturePath = false;
            DebugLog.Write("[send] using CPU frame path");
        }
    }

    public void Stop()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        if (_usingTexturePath)
        {
            _source.TextureArrived -= OnTextureArrived;
        }
        else
        {
            _source.FrameArrived -= OnFrameArrived;
        }

        // One-glance share summary at the end. The startup snapshot,
        // share-start line, and this stop line bracket every share
        // session unambiguously in the log.
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        var elapsedSec = _startWallTicks == 0
            ? 0.0
            : (System.Diagnostics.Stopwatch.GetTimestamp() - _startWallTicks) / freq;
        var avgKbps = elapsedSec > 0 ? (EncodedByteCount * 8.0 / elapsedSec) / 1000.0 : 0.0;
        DebugLog.Write($"[settings-share] stopped after {elapsedSec:F1}s frames={EncodedFrameCount}/{FrameCount} encodedBytes={EncodedByteCount} avgBitrate={avgKbps:F0}kbps");
    }

    /// <summary>
    /// Phase-locked frame throttle. Maintains a monotonic deadline that
    /// advances by one frame period on every accept. Accepts any frame
    /// whose timestamp reaches (or is within jitter tolerance of) the
    /// deadline. This does two things a strict gap check can't:
    ///   - jitter doesn't amplify into doubled intervals (a frame arriving
    ///     1 ms early no longer forces the next kept frame to land 2 × T
    ///     later)
    ///   - rate ratios like 30 fps from a 46 fps source work: we accept
    ///     every frame whose arrival time has caught up with the deadline,
    ///     so we get ~30 fps instead of ~23 fps
    /// On big drift (deadline lagging by more than a frame — pipeline
    /// stall, source paused) we re-anchor the deadline to the current
    /// timestamp so the throttle doesn't try to burn through a backlog.
    /// </summary>
    private bool ShouldAcceptFrame(long frameTs)
    {
        if (_lastAcceptedFrameTicks == long.MinValue)
        {
            _lastAcceptedFrameTicks = frameTs;
            _nextDeadlineTicks = frameTs + _frameGapTicks;
            return true;
        }

        if (frameTs + _frameGapToleranceTicks < _nextDeadlineTicks)
        {
            return false;
        }

        _lastAcceptedFrameTicks = frameTs;
        _nextDeadlineTicks += _frameGapTicks;
        // Re-anchor if we've drifted more than one period behind — prevents
        // the throttle from accepting every incoming frame to "catch up"
        // after a source pause.
        if (_nextDeadlineTicks < frameTs)
        {
            _nextDeadlineTicks = frameTs + _frameGapTicks;
        }
        return true;
    }

    private (int Width, int Height) ResolveEncoderDimensions(int sourceWidth, int sourceHeight)
    {
        int encWidth;
        int encHeight;
        if (_targetHeight == 0 || _targetHeight >= sourceHeight)
        {
            encWidth = sourceWidth;
            encHeight = sourceHeight;
        }
        else
        {
            encHeight = _targetHeight & ~1;
            var derivedWidth = (int)Math.Round((double)sourceWidth * encHeight / sourceHeight);
            encWidth = derivedWidth & ~1;
            if (encWidth < 2)
            {
                encWidth = 2;
            }
        }

        // NVENC's H.264 path can expose padded coded dimensions when an
        // odd-aspect target width is not macroblock-aligned. Align both axes
        // for those odd-width cases, but leave standard widths such as
        // 1920x1080 alone since their vertical crop is handled correctly.
        if (_requiresMacroblockAlignedDimensions && encWidth >= 16 && encWidth % 16 != 0)
        {
            encWidth &= ~15;
            if (encHeight >= 16)
            {
                encHeight &= ~15;
            }
        }

        return (encWidth, encHeight);
    }

    /// <summary>
    /// GPU path handler. Fires inside the framepool callback thread. Runs
    /// the throttle, derives encoder dims from the source aspect on first
    /// frame, and hands the raw texture to the encoder (which does the
    /// GPU scale internally). No compact, no downscale, no CPU copy.
    /// </summary>
    private void OnTextureArrived(IntPtr nativeTexture, int width, int height, TimeSpan timestamp)
    {
        var wallNow = Stopwatch.GetTimestamp();
        var frameTs = timestamp.Ticks;
        // Arrival gap spike: log only when the wall-clock gap between
        // successive captured frames exceeds 2 frame periods at the
        // configured fps. Steady state is silent. The capture provider
        // having a stall (DWM throttling, GPU contention, encoder
        // backpressuring the framepool) shows up here as a multi-frame
        // wallGap.
        if (_lastArrivalWallTicks != 0)
        {
            var ticksPerMs = Stopwatch.Frequency / 1000.0;
            var wallGapMs = (wallNow - _lastArrivalWallTicks) / ticksPerMs;
            if (wallGapMs > ArrivalSpikeThresholdMs)
            {
                var captureGapMs = (frameTs - _lastArrivalFrameTicks) / (double)TimeSpan.TicksPerMillisecond;
                DebugLog.Write($"[arrival-gpu-spike] wallGap={wallGapMs:F1}ms captureGap={captureGapMs:F1}ms (>{ArrivalSpikeThresholdMs:F1}ms @ {_targetFps}fps)");
            }
        }
        _lastArrivalWallTicks = wallNow;
        _lastArrivalFrameTicks = frameTs;

        if (!ShouldAcceptFrame(frameTs))
        {
            return;
        }

        var timingStart = Stopwatch.GetTimestamp();

        SourceWidth = width;
        SourceHeight = height;
        var sourceWidth = width & ~1;
        var sourceHeight = height & ~1;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return;
        }

        var (encWidth, encHeight) = ResolveEncoderDimensions(sourceWidth, sourceHeight);

        // Dimension-change log: fire on first frame and on any source
        // dimension change (windowed share resize, etc). Keeps a clean
        // dimension trace in the log without per-frame spam.
        if (sourceWidth != _lastLoggedSourceWidth || sourceHeight != _lastLoggedSourceHeight)
        {
            DebugLog.Write($"[send-gpu] src {sourceWidth}x{sourceHeight} -> enc {encWidth}x{encHeight}");
            _lastLoggedSourceWidth = sourceWidth;
            _lastLoggedSourceHeight = sourceHeight;
        }

        EncodedFrame? encoded;
        lock (_encodeLock)
        {
            if (_disposed)
            {
                return;
            }

            FrameCount++;

            if (_encoder is null || encWidth != _encoderWidth || encHeight != _encoderHeight)
            {
                ReplaceEncoder(encWidth, encHeight);
            }

            encoded = _encoder!.EncodeTexture(nativeTexture, sourceWidth, sourceHeight, timestamp);

            // If the encoder is async with an external output drain
            // (OutputAvailable event), encoded output is dispatched
            // from the pump thread, not here. Skip inline dispatch.
            if (_asyncOutput is not null)
            {
                encoded = null;
            }

            if (encoded is null || encoded.Value.Bytes.Length == 0)
            {
                LogGpuTiming(timingStart);
                return;
            }

            NoteEncodedFrame(encoded.Value.Bytes.Length);
        }

        // Use the timestamp the encoder propagated through its pipeline,
        // not the timestamp of the input we just submitted. With async
        // hardware encoders the bytes coming out of the pump correspond
        // to an earlier input, so the encoder's propagated SampleTime is
        // what the receiver's PTS-based pacer actually needs.
        var contentTs = encoded.Value.Timestamp;
        var duration = ComputeDurationRtpUnits(contentTs);
        _onEncoded(duration, encoded.Value.Bytes, contentTs);

        LogGpuTiming(timingStart);
    }

    private void LogGpuTiming(long timingStart)
    {
        if (timingStart == 0)
        {
            return;
        }

        var encodeEnd = Stopwatch.GetTimestamp();
        var ticksPerMs = Stopwatch.Frequency / 1000.0;
        var totalMs = (encodeEnd - timingStart) / ticksPerMs;
        // Encode-spike: synchronous encode-path time that exceeded half
        // the per-frame budget. Threshold scales with fps so the
        // intent ("you ate half the wall-time you have") stays
        // constant: 8 ms at 60 fps, 17 ms at 30 fps, 4 ms at 120 fps.
        if (totalMs > EncodeSpikeThresholdMs)
        {
            DebugLog.Write($"[timing-gpu-spike] total={totalMs:F1}ms (>{EncodeSpikeThresholdMs:F1}ms @ {_targetFps}fps)");
        }
    }

    private void OnFrameArrived(in CaptureFrameData frame)
    {
        if (frame.Format != CaptureFramePixelFormat.Bgra8)
        {
            return;
        }

        // Inter-arrival diagnostic: how often does the framepool actually hand
        // us a frame, before any throttling? If this is wider than the target
        // 1/fps gap then the bottleneck is upstream of the encoder (framepool
        // rate or _frameLock contention with the preview path), not us.
        var wallNow = Stopwatch.GetTimestamp();
        var frameTs = frame.Timestamp.Ticks;
        if (_lastArrivalWallTicks != 0)
        {
            var ticksPerMs = Stopwatch.Frequency / 1000.0;
            var wallGapMs = (wallNow - _lastArrivalWallTicks) / ticksPerMs;
            if (wallGapMs > ArrivalSpikeThresholdMs)
            {
                var captureGapMs = (frameTs - _lastArrivalFrameTicks) / (double)TimeSpan.TicksPerMillisecond;
                DebugLog.Write($"[arrival-spike] wallGap={wallGapMs:F1}ms captureGap={captureGapMs:F1}ms (>{ArrivalSpikeThresholdMs:F1}ms @ {_targetFps}fps)");
            }
        }
        _lastArrivalWallTicks = wallNow;
        _lastArrivalFrameTicks = frameTs;

        if (!ShouldAcceptFrame(frameTs))
        {
            return;
        }

        var timingStart = Stopwatch.GetTimestamp();

        // Source crop: even dimensions (libvpx chroma subsampling) and non-negative.
        SourceWidth = frame.Width;
        SourceHeight = frame.Height;
        var sourceWidth = frame.Width & ~1;
        var sourceHeight = frame.Height & ~1;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return;
        }

        // Decide the encoder target. The user picks a target height; width is
        // derived from the source aspect ratio at runtime so the output never
        // distorts. _targetHeight == 0 (or >= source) means "don't downscale".
        var (encWidth, encHeight) = ResolveEncoderDimensions(sourceWidth, sourceHeight);

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

        var compactEnd = Stopwatch.GetTimestamp();

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

        var downscaleEnd = Stopwatch.GetTimestamp();

        if (sourceWidth != _lastLoggedSourceWidth || sourceHeight != _lastLoggedSourceHeight)
        {
            DebugLog.Write($"[send] src w={frame.Width} h={frame.Height} stride={frame.StrideBytes} -> enc w={encWidth} h={encHeight}");
            _lastLoggedSourceWidth = sourceWidth;
            _lastLoggedSourceHeight = sourceHeight;
        }

        var timestamp = frame.Timestamp;
        EncodeAndEmit(encoderInput, encoderInputStride, encWidth, encHeight, timestamp);

        var encodeEndCpu = Stopwatch.GetTimestamp();
        var ticksPerMsCpu = Stopwatch.Frequency / 1000.0;
        var totalMsCpu = (encodeEndCpu - timingStart) / ticksPerMsCpu;
        // Spike-only — same fps-derived threshold as the GPU path. Logs
        // the per-stage breakdown only when total exceeded half the
        // per-frame budget at the configured fps.
        if (totalMsCpu > EncodeSpikeThresholdMs)
        {
            var compactMs = (compactEnd - timingStart) / ticksPerMsCpu;
            var downscaleMs = (downscaleEnd - compactEnd) / ticksPerMsCpu;
            var encodeMs = (encodeEndCpu - downscaleEnd) / ticksPerMsCpu;
            DebugLog.Write(
                $"[timing-spike] compact={compactMs:F1}ms downscale={downscaleMs:F1}ms encode={encodeMs:F1}ms total={totalMsCpu:F1}ms (>{EncodeSpikeThresholdMs:F1}ms @ {_targetFps}fps)");
        }
    }

    private void EncodeAndEmit(byte[] encoderInput, int encoderStrideBytes, int width, int height, TimeSpan timestamp)
    {
        EncodedFrame? encoded;
        lock (_encodeLock)
        {
            if (_disposed)
            {
                return;
            }

            FrameCount++;

            // Rebuild the encoder if the stream dimensions changed. Both libvpx
            // and Media Foundation lock their encoder state to the first
            // frame's dimensions and cannot retarget on the fly, so the
            // abstraction contract is: one encoder instance per (width,height)
            // tuple and the caller swaps them out on change.
            if (_encoder is null || width != _encoderWidth || height != _encoderHeight)
            {
                ReplaceEncoder(width, height);
            }

            encoded = _encoder!.EncodeBgra(encoderInput, encoderStrideBytes, timestamp);

            if (_asyncOutput is not null)
            {
                encoded = null;
            }

            if (encoded is null || encoded.Value.Bytes.Length == 0)
            {
                return;
            }

            NoteEncodedFrame(encoded.Value.Bytes.Length);
        }

        var contentTs = encoded.Value.Timestamp;
        var duration = ComputeDurationRtpUnits(contentTs);
        _onEncoded(duration, encoded.Value.Bytes, contentTs);
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
        if (rtpUnits <= 0)
        {
            rtpUnits = rtpClockRateHz / 60;
        }

        if (rtpUnits > rtpClockRateHz)
        {
            rtpUnits = rtpClockRateHz;
        }

        return (uint)rtpUnits;
    }

    /// <summary>
    /// Tear down the old encoder (if any) and build a new one at the
    /// given dimensions. If the new encoder implements
    /// <see cref="IAsyncEncodedOutputSource"/>, subscribe to its
    /// <see cref="IAsyncEncodedOutputSource.OutputAvailable"/> event
    /// so encoded frames are dispatched the instant the hardware
    /// encoder produces them, not delayed until the next capture
    /// callback polls the output queue.
    /// </summary>
    private void ReplaceEncoder(int width, int height)
    {
        // Unsubscribe from the old async source before disposing.
        if (_asyncOutput is not null)
        {
            _asyncOutput.OutputAvailable -= OnAsyncEncoderOutput;
            _asyncOutput = null;
        }
        try { _encoder?.Dispose(); } catch (Exception ex) { DebugLog.Write($"[send] encoder dispose threw: {ex.GetType().Name}: {ex.Message}"); }

        _encoder = _encoderFactory.CreateEncoder(width, height, _targetFps, _targetBitrate, _gopFrames);
        _encoderWidth = width;
        _encoderHeight = height;

        if (_encoder is IAsyncEncodedOutputSource asyncSrc)
        {
            _asyncOutput = asyncSrc;
            asyncSrc.OutputAvailable += OnAsyncEncoderOutput;
        }

        // Resolved-config snapshot. The encoder's own factory/selector
        // already logs which backend it picked; this line ties the
        // encoder identity to the dimensions/fps/bitrate/GOP that
        // CaptureStreamer requested for this share. Together they're
        // enough to reconstruct the encoder spec from the log alone.
        DebugLog.Write($"[settings-share] encoder={_encoder.GetType().Name} codec={_encoder.Codec} {width}x{height}@{_targetFps} {_targetBitrate / 1000}kbps gop={_gopFrames}f path={(_usingTexturePath ? "GPU-texture" : "CPU")}");
    }

    /// <summary>
    /// Fires on the encoder's async event pump thread the instant a
    /// new encoded frame is enqueued. Drains all available output and
    /// dispatches via <see cref="_onEncoded"/> immediately — no waiting
    /// for the next capture callback.
    /// </summary>
    private void OnAsyncEncoderOutput()
    {
        if (_disposed || _asyncOutput is null)
        {
            return;
        }

        while (_asyncOutput.TryDequeueEncoded(out var frame))
        {
            if (frame.Bytes is null || frame.Bytes.Length == 0)
            {
                continue;
            }

            NoteEncodedFrame(frame.Bytes.Length);

            var duration = ComputeDurationRtpUnits(frame.Timestamp);
            _onEncoded(duration, frame.Bytes, frame.Timestamp);
        }
    }

    /// <summary>
    /// Propagate a keyframe request from upstream (e.g. an RTCP PLI received
    /// by <see cref="Services.WebRtcSession"/>) down into the encoder.
    /// Best-effort: if the encoder is null or being torn down, silently
    /// swallow — the next natural keyframe still arrives on the scheduled
    /// GOP boundary.
    ///
    /// Debounced. There are at least three upstream paths that all end
    /// up here: the legacy signaling-channel <c>RequestKeyframe</c>
    /// message (fired once per subscriber Connected), RTCP PLI from a
    /// subscriber forwarded by the SFU, and any future internal trigger
    /// (e.g. a reaction to extreme upstream loss). When two or three
    /// arrive in the same 500 ms window — say, a subscriber Connected
    /// at the same moment they hit a packet-loss-induced PLI — we want
    /// exactly one forced IDR, not three. The encoder also benefits:
    /// emitting an IDR every frame for half a second produces a
    /// massive bitrate spike that defeats the recovery it's trying to
    /// support. <see cref="ForceIdrMinIntervalMs"/> caps that at one
    /// per ~half-GOP.
    /// </summary>
    private const int ForceIdrMinIntervalMs = 500;
    private readonly System.Diagnostics.Stopwatch _lastForceIdrAt = new();

    public void RequestKeyframe()
    {
        if (_disposed)
        {
            return;
        }
        lock (_lastForceIdrAt)
        {
            if (_lastForceIdrAt.IsRunning && _lastForceIdrAt.ElapsedMilliseconds < ForceIdrMinIntervalMs)
            {
                VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                    $"[capture] RequestKeyframe suppressed (debounce, {_lastForceIdrAt.ElapsedMilliseconds}ms since last)");
                return;
            }
            _lastForceIdrAt.Restart();
        }

        VicoScreenShare.Client.Diagnostics.DebugLog.Write("[capture] RequestKeyframe -> encoder.RequestKeyframe()");
        try { _encoder?.RequestKeyframe(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        lock (_encodeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_asyncOutput is not null)
            {
                _asyncOutput.OutputAvailable -= OnAsyncEncoderOutput;
                _asyncOutput = null;
            }
            try { _encoder?.Dispose(); } catch { }
            _encoder = null;
        }
    }
}
