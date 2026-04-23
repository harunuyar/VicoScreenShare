namespace VicoScreenShare.Client.Media;

using System;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;

/// <summary>
/// Bridges a platform <see cref="IAudioCaptureSource"/> to a SIPSorcery
/// <c>RTCPeerConnection.SendAudio</c> callback, mirroring the shape of
/// <see cref="CaptureStreamer"/> for the video path.
///
/// Pipeline per capture callback:
/// <list type="number">
/// <item>Resample / format-convert via <see cref="IAudioResampler"/> to
///       48 kHz interleaved S16 (pass-through when the device already
///       delivers 48 kHz, which is every modern shared-mode WASAPI
///       endpoint on Windows).</item>
/// <item>Accumulate into a ring-ish buffer keyed by frame size
///       (<c>encoder.FrameSamples × encoder.Channels</c>). Opus is a
///       fixed-frame codec; WASAPI produces variable-sized buffers.</item>
/// <item>For every complete frame that accumulates, encode and emit via
///       the <see cref="Action{TDuration, TBytes, TContentTs}"/>
///       callback. Production wires this to <c>RTCPeerConnection.SendAudio</c>.
///       Duration in RTP units is fixed at <c>encoder.FrameSamples</c>
///       (960 for 20 ms @ 48 kHz) — Opus's RTP clock rate and sample
///       rate coincide.</item>
/// </list>
///
/// Start / Stop / Dispose are serialized under a mutex, same reasoning
/// as <see cref="CaptureStreamer._encodeLock"/>: a stop racing with an
/// in-flight encode inside a native codec can crash the CLR. Concentus
/// is managed so the crash mode doesn't apply, but the invariant is
/// worth keeping for library-swap robustness.
/// </summary>
public sealed class AudioStreamer : IDisposable
{
    private readonly IAudioCaptureSource _source;
    private readonly IAudioResampler _resampler;
    private readonly IAudioEncoderFactory _encoderFactory;
    private readonly AudioSettings _settings;
    private readonly Action<uint, byte[], TimeSpan> _onEncoded;
    private readonly object _encodeLock = new();

    private IAudioEncoder? _encoder;
    private int _frameStride;
    private short[] _accumulator = Array.Empty<short>();
    private int _accumulatorCount;
    private short[] _resampleScratch = Array.Empty<short>();

    private bool _attached;
    private bool _disposed;
    private long _framesEmitted;
    private long _bytesEmitted;
    private TimeSpan _latestTimestamp;

    public AudioStreamer(
        IAudioCaptureSource source,
        IAudioResampler resampler,
        Action<uint, byte[], TimeSpan> onEncoded,
        AudioSettings settings,
        IAudioEncoderFactory encoderFactory)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resampler);
        ArgumentNullException.ThrowIfNull(onEncoded);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(encoderFactory);

        _source = source;
        _resampler = resampler;
        _onEncoded = onEncoded;
        _settings = settings;
        _encoderFactory = encoderFactory;
    }

    /// <summary>Number of Opus frames the encoder has produced since
    /// <see cref="Start"/>. Updated under the encode lock.</summary>
    public long FramesEmitted => System.Threading.Interlocked.Read(ref _framesEmitted);

    /// <summary>Cumulative encoded bytes. Used by the stats panel to
    /// compute audio bitrate between polls.</summary>
    public long BytesEmitted => System.Threading.Interlocked.Read(ref _bytesEmitted);

    /// <summary>Most recent capture-side timestamp that reached the
    /// encoder. Useful for sync diagnostics alongside
    /// <see cref="CaptureStreamer"/>'s last-frame timestamp.</summary>
    public TimeSpan LatestTimestamp => _latestTimestamp;

    public void Start()
    {
        lock (_encodeLock)
        {
            if (_attached || _disposed)
            {
                return;
            }
            _encoder = _encoderFactory.CreateEncoder(_settings);
            _frameStride = _encoder.FrameSamples * _encoder.Channels;
            // Pre-size the accumulator for 8 frames worth — WASAPI can
            // deliver up to ~60 ms buffers in shared mode, and 8 × 20 ms
            // comfortably absorbs that plus any resampler edge rounding
            // without a realloc on the hot path.
            _accumulator = new short[_frameStride * 8];
            _accumulatorCount = 0;
            // Resample scratch sized for 2× max WASAPI buffer. The
            // publisher's buffers are typically 10-30 ms (~1.4-4.5 KB
            // of S16 stereo); 8 frames worth is plenty.
            _resampleScratch = new short[_frameStride * 8];
            _attached = true;
        }
        _source.FrameArrived += OnFrameArrived;
    }

    public void Stop()
    {
        bool wasAttached;
        lock (_encodeLock)
        {
            wasAttached = _attached;
            _attached = false;
        }
        if (wasAttached)
        {
            _source.FrameArrived -= OnFrameArrived;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Stop();
        lock (_encodeLock)
        {
            _disposed = true;
            try { _encoder?.Dispose(); } catch { }
            _encoder = null;
        }
    }

    private void OnFrameArrived(in AudioFrameData frame)
    {
        // Capture span needs to be consumed under the lock so a racing
        // Dispose can't tear down the encoder mid-encode. Resampling is
        // small (microseconds); the lock is held for <1 ms in practice.
        lock (_encodeLock)
        {
            if (!_attached || _disposed || _encoder is null)
            {
                return;
            }

            int produced;
            try
            {
                produced = _resampler.Resample(
                    frame.Pcm,
                    frame.SampleRate,
                    frame.Channels,
                    frame.Format,
                    _resampleScratch);
            }
            catch
            {
                // Resampler fault: skip this buffer, don't tear down.
                return;
            }

            if (produced == 0)
            {
                return;
            }

            // Concentus is mono/stereo only; if the source delivered a
            // different channel count the resampler is a pass-through on
            // channel shape, so mismatches would manifest here as a
            // shape mismatch with the encoder. Guard explicitly.
            if (frame.Channels != _encoder.Channels)
            {
                return;
            }

            // Append to accumulator, growing only if the fixed-size
            // allocation (8 frames worth) was too conservative.
            if (_accumulatorCount + produced > _accumulator.Length)
            {
                var bigger = new short[(_accumulatorCount + produced) * 2];
                Array.Copy(_accumulator, bigger, _accumulatorCount);
                _accumulator = bigger;
            }
            Array.Copy(_resampleScratch, 0, _accumulator, _accumulatorCount, produced);
            _accumulatorCount += produced;

            // Drain exact-size frames. Opus does not support VBR frame
            // duration on the encoder input; we slice the accumulator
            // into 960 × channel chunks and submit them one at a time.
            // The content timestamp attached to each encoded packet is
            // the capture buffer's timestamp — not quite the timestamp
            // of that exact sub-frame's first sample, but close enough
            // (errors are bounded by the buffer size ≤ 60 ms) and
            // RTP-synchronous viewers won't notice the 10-ms max skew.
            while (_accumulatorCount >= _frameStride)
            {
                var slice = _accumulator.AsSpan(0, _frameStride);
                EncodedAudioFrame? encoded;
                try
                {
                    encoded = _encoder.EncodePcm(slice, frame.Timestamp);
                }
                catch
                {
                    // Shift and continue.
                    ShiftAccumulator();
                    continue;
                }

                ShiftAccumulator();
                if (encoded is null || encoded.Value.Bytes.Length == 0)
                {
                    continue;
                }

                System.Threading.Interlocked.Increment(ref _framesEmitted);
                System.Threading.Interlocked.Add(ref _bytesEmitted, encoded.Value.Bytes.Length);
                _latestTimestamp = frame.Timestamp;

                // RTP duration in Opus's 48 kHz clock equals samples per
                // channel. 960 for a 20 ms frame. SendAudio expects this
                // as the first arg and uses it to advance its internal
                // RTP timestamp — the wire-visible RTP ts increments by
                // 960 per packet.
                var durationRtp = (uint)encoded.Value.Samples;
                try
                {
                    _onEncoded(durationRtp, encoded.Value.Bytes, frame.Timestamp);
                }
                catch
                {
                    // SendAudio can throw if the PC is mid-teardown;
                    // don't propagate into the capture thread.
                }
            }
        }
    }

    private void ShiftAccumulator()
    {
        var remainder = _accumulatorCount - _frameStride;
        if (remainder > 0)
        {
            Array.Copy(_accumulator, _frameStride, _accumulator, 0, remainder);
        }
        _accumulatorCount = remainder;
    }
}
