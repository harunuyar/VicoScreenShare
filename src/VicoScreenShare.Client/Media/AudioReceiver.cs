namespace VicoScreenShare.Client.Media;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;

/// <summary>
/// Receiver-side counterpart to <see cref="AudioStreamer"/>. Subscribes
/// to a <see cref="RTCPeerConnection"/>'s <c>OnRtpPacketReceived</c>
/// filtered to <see cref="SDPMediaTypesEnum.audio"/>, decodes each Opus
/// packet, sorts by RTP timestamp in a small jitter buffer, and pushes
/// PCM into an <see cref="IAudioRenderer"/>.
/// <para>
/// Deliberately simpler than <see cref="StreamReceiver"/>: no keyframe
/// concept, no drop-to-IDR, no bounded-channel decode worker, no
/// GPU-texture handoff. Opus frames are independent 20 ms units, decode
/// is sub-millisecond, and WASAPI already absorbs short underflows as
/// silence. A 3-slot jitter buffer (60 ms) is plenty; more would just
/// grow end-to-end latency without buying robustness.
/// </para>
/// <para>
/// This class owns one decoder and one renderer — the
/// <see cref="SubscriberSession"/> that builds us supplies both from
/// the process-wide factories. Disposal tears them down; concurrent
/// decode is serialized under a lock the same way <see cref="StreamReceiver"/>
/// serializes video decode (SIPSorcery's receive thread vs. our stop
/// path racing into the codec's free).
/// </para>
/// </summary>
public sealed class AudioReceiver : IAsyncDisposable
{
    // Steady-state jitter buffer depth in packets. 3 × 20 ms = 60 ms
    // is enough to cover typical Wi-Fi reorder windows once the
    // stream is locked to video; we only drain once the buffer
    // exceeds this depth.
    private const int JitterBufferMaxPackets = 3;

    // Hard cap on the buffer overall. Pre-MediaClock-anchor we
    // accumulate every audio frame so nothing is lost while video's
    // playout buffer fills (largest reasonable setting: 240 frames
    // at 60 fps = 4 s of pre-paint audio). 500 packets = 10 s of
    // 20 ms Opus framing — comfortable for any user-visible buffer
    // setting, hard-capped so a stuck stream can't OOM.
    private const int JitterBufferHardCap = 500;

    private readonly RTCPeerConnection _pc;
    private readonly IAudioDecoderFactory _decoderFactory;
    private readonly IAudioRenderer _renderer;
    private readonly MediaClock? _mediaClock;
    private readonly string _displayName;
    private readonly object _decodeLock = new();
    private readonly SortedList<uint, DecodedAudioFrame> _jitterBuffer = new(JitterBufferMaxPackets + 2);

    private IAudioDecoder? _decoder;
    private uint _lastDrainedRtpTimestamp;
    private bool _hasDrained;
    // Audio-SR fallback: when the video anchor latches but audio
    // SR hasn't arrived yet (SIPSorcery's RTCP cadence is ~5 s,
    // so this is the common case at session start), we synthesize
    // a local audio anchor: the oldest queued audio packet at the
    // moment of video anchor is treated as if its publisher capture
    // time matches the video anchor's. From there audio plays at
    // 48 kHz against the same wall clock the video render uses.
    // Once a real audio SR arrives, MediaClock takes over and the
    // fallback is retired — the two anchors agree to within a few
    // ms because audio capture starts within ms of video capture
    // on the publisher.
    private bool _fallbackActive;
    private uint _fallbackAnchorRtp;
    private long _fallbackAnchorLocalTicks;
    // Sync re-check throttle. Once the audio stream is locked to the
    // video clock (within tolerance), we don't run the alignment
    // logic again for SyncReCheckInterval. This avoids the
    // micro-correction churn that produced the brief noise + silence
    // glitches we used to hear: WASAPI is happy to play continuous
    // audio at the device's nominal rate; the only times we MUST
    // re-align are after big stalls (publisher pause, GC, network
    // burst) which are rare.
    private long _nextSyncCheckTicks;
    private static readonly TimeSpan SyncReCheckInterval = TimeSpan.FromSeconds(5);
    // |error| <= this is considered "in sync" — we just submit the
    // frame as-is and skip alignment work.
    private const long SyncToleranceMicros = 60_000; // 60 ms
    private System.Threading.Timer? _pulseTimer;
    private long _pulseLastReceived;
    private long _pulseLastDecoded;
    private long _pulseLastSubmitted;
    private long _pulseLastDropped;
    private long _pulseLastPadded;
    private long _pulseLastSkipped;
    private long _packetsReceived;
    private long _framesDecoded;
    private long _framesSubmitted;
    private long _framesDropped;
    private long _samplesPaddedTotal;
    private long _samplesSkippedTotal;
    private long _lastOffsetMicros;
    private int _state; // 0 = idle, 1 = running, 2 = disposed

    public AudioReceiver(
        RTCPeerConnection pc,
        IAudioDecoderFactory decoderFactory,
        IAudioRenderer renderer,
        string displayName)
        : this(pc, decoderFactory, renderer, displayName, mediaClock: null)
    {
    }

    public AudioReceiver(
        RTCPeerConnection pc,
        IAudioDecoderFactory decoderFactory,
        IAudioRenderer renderer,
        string displayName,
        MediaClock? mediaClock)
    {
        ArgumentNullException.ThrowIfNull(pc);
        ArgumentNullException.ThrowIfNull(decoderFactory);
        ArgumentNullException.ThrowIfNull(renderer);

        _pc = pc;
        _decoderFactory = decoderFactory;
        _renderer = renderer;
        _mediaClock = mediaClock;
        _displayName = displayName ?? string.Empty;
    }

    /// <summary>Number of audio RTP packets observed since <see cref="StartAsync"/>.</summary>
    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    /// <summary>Number of packets that produced a decoded frame
    /// (successful decode + valid PCM).</summary>
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);

    /// <summary>Number of decoded frames actually pushed into the
    /// renderer (drained from the jitter buffer in RTP-timestamp order).</summary>
    public long FramesSubmitted => Interlocked.Read(ref _framesSubmitted);

    /// <summary>
    /// Number of frames discarded — either late-arriving reorders
    /// already past the drain cursor, or jitter-buffer overflow drops.
    /// </summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>
    /// Cumulative count of zero-PCM samples prepended by the
    /// MediaClock-driven A/V sync alignment. Non-zero only on stream
    /// start / re-anchor: once the first audio frame is aligned to
    /// video's wall-clock schedule, subsequent frames flow without
    /// padding.
    /// </summary>
    public long SamplesPadded => Interlocked.Read(ref _samplesPaddedTotal);

    /// <summary>
    /// Cumulative count of PCM samples discarded from the head of an
    /// incoming frame because audio was running ahead of the
    /// MediaClock-derived target wall-clock.
    /// </summary>
    public long SamplesSkipped => Interlocked.Read(ref _samplesSkippedTotal);

    /// <summary>
    /// Last observed offset between the audio frame's MediaClock
    /// target wall-clock and the moment we actually submitted it,
    /// in microseconds. Positive = audio is late (we played it late
    /// or skipped some samples to catch up); negative = audio is
    /// early (we silence-padded). Zero (or null) until both an audio
    /// SR and the shared anchor are present. Drives the av-sync
    /// stats line.
    /// </summary>
    public long LastOffsetMicros => Interlocked.Read(ref _lastOffsetMicros);

    /// <summary>
    /// When true, incoming audio RTP packets are dropped at the socket
    /// boundary before they reach the decoder — muted streams cost no
    /// CPU beyond the RTP header walk SIPSorcery already does. The
    /// renderer is also silenced via <see cref="IAudioRenderer.Volume"/>
    /// so any already-queued samples flush as silence.
    /// </summary>
    public bool IsMuted
    {
        get => Volatile.Read(ref _isMuted) != 0;
        set
        {
            Volatile.Write(ref _isMuted, value ? 1 : 0);
            if (value)
            {
                // Drop anything already queued so unmuting doesn't
                // dump a stale burst.
                lock (_decodeLock)
                {
                    _jitterBuffer.Clear();
                }
            }
        }
    }

    private int _isMuted;

    /// <summary>
    /// Linear [0, 1] volume forwarded to the renderer — 1.0 is
    /// unattenuated, 0.0 is silence. Separate from
    /// <see cref="IsMuted"/> so the UI can let the user pick a target
    /// volume with mute engaged and unmute to exactly that level.
    /// </summary>
    public double Volume
    {
        get => _renderer.Volume;
        set => _renderer.Volume = value;
    }

    public async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return;
        }

        // Opus is always 48 kHz WebRTC; channels derive from the SDP
        // negotiation but SIPSorcery does not surface the negotiated
        // channel count in a form the receiver can read synchronously,
        // and the publisher drives this end-to-end anyway. Default to
        // stereo — matches AudioCommonlyUsedFormats.OpusWebRTC.
        const int channels = 2;
        _decoder = _decoderFactory.CreateDecoder(channels);
        await _renderer.StartAsync(_decoder.SampleRate, _decoder.Channels).ConfigureAwait(false);

        _pc.OnRtpPacketReceived += OnRtpPacketReceived;
        _pulseTimer = new System.Threading.Timer(_ => EmitPulse(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void EmitPulse()
    {
        if (_state != 1)
        {
            return;
        }
        var received = Interlocked.Read(ref _packetsReceived);
        var decoded = Interlocked.Read(ref _framesDecoded);
        var submitted = Interlocked.Read(ref _framesSubmitted);
        var dropped = Interlocked.Read(ref _framesDropped);
        var padded = Interlocked.Read(ref _samplesPaddedTotal);
        var skipped = Interlocked.Read(ref _samplesSkippedTotal);
        var offset = Interlocked.Read(ref _lastOffsetMicros);
        var dRcv = received - _pulseLastReceived; _pulseLastReceived = received;
        var dDec = decoded - _pulseLastDecoded; _pulseLastDecoded = decoded;
        var dSub = submitted - _pulseLastSubmitted; _pulseLastSubmitted = submitted;
        var dDrop = dropped - _pulseLastDropped; _pulseLastDropped = dropped;
        var dPad = padded - _pulseLastPadded; _pulseLastPadded = padded;
        var dSkip = skipped - _pulseLastSkipped; _pulseLastSkipped = skipped;
        if (dRcv == 0 && dDec == 0 && dSub == 0 && dDrop == 0 && dPad == 0 && dSkip == 0)
        {
            return;
        }
        int qDepth;
        lock (_decodeLock)
        {
            qDepth = _jitterBuffer.Count;
        }
        DebugLog.Write(
            $"[audio-rx-pulse {_displayName}] 2s: rcvd=+{dRcv} dec=+{dDec} sub=+{dSub} drop=+{dDrop} "
            + $"pad=+{dPad} skip=+{dSkip} q={qDepth} offsetMs={offset / 1000.0:F1}");
    }

    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 0, 1) != 1)
        {
            return;
        }
        _pc.OnRtpPacketReceived -= OnRtpPacketReceived;
        try { _pulseTimer?.Dispose(); } catch { }
        _pulseTimer = null;
        await _renderer.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _state, 2) == 2)
        {
            return;
        }
        try { _pc.OnRtpPacketReceived -= OnRtpPacketReceived; } catch { }
        try { _pulseTimer?.Dispose(); } catch { }
        _pulseTimer = null;
        // Take the decode lock before freeing the decoder so a racing
        // OnRtpPacketReceived callback can't feed bytes into a freed
        // native state. Managed Opus is safer than libvpx here, but the
        // invariant keeps the class robust if the backend swaps.
        lock (_decodeLock)
        {
            try { _decoder?.Dispose(); } catch { }
            _decoder = null;
            _jitterBuffer.Clear();
        }
        try { await _renderer.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// Test seam. Feeds an RTP-shaped payload directly into the receive
    /// path without needing a real peer connection. Only used from
    /// <c>AudioReceiverTests</c>.
    /// </summary>
    internal void IngestPacket(uint rtpTimestamp, ReadOnlySpan<byte> payload)
    {
        DecodeAndSubmit(rtpTimestamp, payload);
    }

    private void OnRtpPacketReceived(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
    {
        if (_state != 1 || mediaType != SDPMediaTypesEnum.audio || packet?.Payload is null)
        {
            return;
        }
        Interlocked.Increment(ref _packetsReceived);
        if (Volatile.Read(ref _isMuted) != 0)
        {
            // Muted: skip decode entirely. We still count packets as
            // received (so the stats overlay shows the publisher's
            // audio arrived, just not played) but nothing else runs.
            return;
        }
        try
        {
            DecodeAndSubmit(packet.Header.Timestamp, packet.Payload);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[audio-rx {_displayName}] decode threw: {ex.Message}");
        }
    }

    private void DecodeAndSubmit(uint rtpTimestamp, ReadOnlySpan<byte> payload)
    {
        DecodedAudioFrame decoded;
        lock (_decodeLock)
        {
            if (_decoder is null)
            {
                return;
            }
            var result = _decoder.Decode(payload, rtpTimestamp);
            if (result is null)
            {
                return;
            }
            decoded = result.Value;
            Interlocked.Increment(ref _framesDecoded);

            // Drop any frame whose RTP timestamp is older than the most
            // recently drained one — that packet is a late reorder and
            // playing it now would desync the renderer's clock.
            if (_hasDrained && IsOlderOrEqual(rtpTimestamp, _lastDrainedRtpTimestamp))
            {
                Interlocked.Increment(ref _framesDropped);
                return;
            }

            // Dedupe on exact timestamp: a retransmit / duplicate
            // (rare but possible) should not produce two audible
            // frames. Keep the first and drop the rest.
            if (_jitterBuffer.ContainsKey(rtpTimestamp))
            {
                Interlocked.Increment(ref _framesDropped);
                return;
            }
            _jitterBuffer.Add(rtpTimestamp, decoded);

            // Overflow: hard-cap. Pre-anchor we hold every audio
            // frame so we don't lose audio while video's playout
            // buffer fills (the user's ReceiveBufferFrames may delay
            // first paint by seconds). Once the anchor lands we
            // drain at the steady-state cadence below. Drop oldest
            // only when we exceed the absolute hard-cap — covers a
            // permanently-stalled stream OOM.
            while (_jitterBuffer.Count > JitterBufferHardCap)
            {
                _jitterBuffer.RemoveAt(0);
                Interlocked.Increment(ref _framesDropped);
            }
        }

        // Drain — but only once the buffer is past its fill line so the
        // renderer sees a steady trickle rather than a burst + stall.
        // The drain cadence is implicit: every incoming packet can
        // release at most one queued packet, so a sender emitting 50
        // frames/sec produces 50 drains/sec and the buffer stays at its
        // target depth.
        DrainReadyFrames();
    }

    private void DrainReadyFrames()
    {
        // Drain rule when the clock is locked: keep popping while
        // the FRONT of the queue is past its wall-clock target by
        // more than the tolerance — those frames are too late to
        // play in sync, drop them silently here so SubmitAligned
        // never sees them. As soon as the front frame's target is
        // within the tolerance window, hand it to SubmitAligned.
        // Stop when the front frame is too far in the future (the
        // wait gate). This collapses the catchup phase into a
        // single drain call — no more "drop one frame per arriving
        // packet" stutter that produced the 5 s silent opening.
        //
        // Sessions without a MediaClock (tests, audio-only) keep
        // the original count-based jitter rule.
        var clock = _mediaClock;
        const long readyToleranceMicros = 50_000; // 50 ms

        if (clock is null)
        {
            // No-sync path: original behaviour — hold a 3-slot
            // jitter window, drain on overflow.
            while (true)
            {
                DecodedAudioFrame toSubmit;
                lock (_decodeLock)
                {
                    if (_jitterBuffer.Count <= JitterBufferMaxPackets)
                    {
                        return;
                    }
                    var earliestKey = _jitterBuffer.Keys[0];
                    toSubmit = _jitterBuffer.Values[0];
                    _jitterBuffer.RemoveAt(0);
                    _hasDrained = true;
                    _lastDrainedRtpTimestamp = earliestKey;
                }
                try
                {
                    SubmitAligned(toSubmit);
                    Interlocked.Increment(ref _framesSubmitted);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[audio-rx {_displayName}] renderer submit threw: {ex.Message}");
                }
            }
        }

        // Sync path. We have a MediaClock; check whether MediaClock
        // can already give us SR-based wall targets (precise path),
        // and otherwise activate a fallback anchor as soon as
        // PaintLoop has set the paint-anchor — that's the moment
        // video starts on the screen, and we want audio to start
        // there too. The fallback uses the oldest queued audio
        // packet at the moment of paint as a proxy for "audio
        // captured at the same publisher instant as the painted
        // video frame" — accurate to within the audio/video
        // capture-start skew on the publisher (typically tens of
        // ms).
        if (!_fallbackActive)
        {
            var paint = clock.GetPaintAnchor();
            if (paint is { } p)
            {
                lock (_decodeLock)
                {
                    if (_jitterBuffer.Count > 0)
                    {
                        _fallbackAnchorRtp = _jitterBuffer.Keys[0];
                        _fallbackAnchorLocalTicks = p.LocalStopwatchTicks;
                        _fallbackActive = true;
                        DebugLog.Write(
                            $"[audio-rx {_displayName}] fallback anchor active: rtpTs={_fallbackAnchorRtp} "
                            + $"localAnchor={_fallbackAnchorLocalTicks} videoContent={p.VideoContentTime.TotalMilliseconds:F0}ms");
                    }
                }
            }
        }

        while (true)
        {
            DecodedAudioFrame toSubmit;
            lock (_decodeLock)
            {
                if (_jitterBuffer.Count == 0)
                {
                    return;
                }
                var earliestKey = _jitterBuffer.Keys[0];
                var targetTicks = clock.AudioRtpToLocalWallTicks(earliestKey);
                if (targetTicks is null && _fallbackActive)
                {
                    // Compute target from the fallback anchor.
                    // delta in seconds = (rtpTs − fallback_rtp) / 48000.
                    var deltaSamples = (long)(int)(earliestKey - _fallbackAnchorRtp);
                    var deltaTicks = deltaSamples * Stopwatch.Frequency / 48_000L;
                    targetTicks = _fallbackAnchorLocalTicks + deltaTicks;
                }
                if (targetTicks is null)
                {
                    // Pre-anchor — accumulate.
                    return;
                }
                var nowTicks = Stopwatch.GetTimestamp();
                var deltaMicros = (targetTicks.Value - nowTicks)
                    * 1_000_000L / Stopwatch.Frequency;

                if (deltaMicros > readyToleranceMicros)
                {
                    // Front is in the future — wait for time to
                    // catch up. Next audio packet wakes us.
                    return;
                }

                if (deltaMicros < -readyToleranceMicros)
                {
                    // Front is too late — drop it without going
                    // through SubmitAligned, then check the next.
                    // This is the catchup-burst path: a 5 s
                    // backlog of frames whose targets were all in
                    // the past becomes a single tight loop here,
                    // not 5 s of one-drop-per-arriving-packet.
                    Interlocked.Add(ref _samplesSkippedTotal, _jitterBuffer.Values[0].Samples);
                    Interlocked.Increment(ref _framesDropped);
                    _jitterBuffer.RemoveAt(0);
                    _hasDrained = true;
                    _lastDrainedRtpTimestamp = earliestKey;
                    continue;
                }

                // Front is within ±50 ms of now — playable.
                toSubmit = _jitterBuffer.Values[0];
                _jitterBuffer.RemoveAt(0);
                _hasDrained = true;
                _lastDrainedRtpTimestamp = earliestKey;
            }

            try
            {
                SubmitAligned(toSubmit);
                Interlocked.Increment(ref _framesSubmitted);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[audio-rx {_displayName}] renderer submit threw: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Push PCM into the renderer, aligned to the MediaClock-derived
    /// target wall time when the anchor has latched.
    ///
    /// Strategy is deliberately simple to avoid the micro-correction
    /// churn earlier iterations produced (brief noise + silence
    /// glitches as we trimmed/padded every frame):
    /// <list type="bullet">
    /// <item>Sessions without a MediaClock or pre-anchor frames pass
    /// through to the renderer immediately. The drain-time gate
    /// already prevents pre-anchor frames from reaching here.</item>
    /// <item>Right after the anchor (or on a periodic re-check), we
    /// look at the front of the queue: if the front's target is in
    /// the past beyond the tolerance, drop frames until the front is
    /// no longer late; if the front's target is in the future beyond
    /// the tolerance, the drain-time gate is already holding it.
    /// Either way the very next submit lands within tolerance.</item>
    /// <item>Once aligned, submit pcm as-is for the next
    /// <see cref="SyncReCheckInterval"/>. The publisher and receiver
    /// audio device clocks are both nominally 48 kHz; tens-of-ppm
    /// drift over 5 s is &lt; 1 ms — well below the tolerance — so
    /// re-alignment isn't needed more often.</item>
    /// </list>
    /// </summary>
    private void SubmitAligned(DecodedAudioFrame frame)
    {
        var pcm = frame.Pcm;
        var pcmTimestamp = TimeSpan.FromTicks((long)frame.RtpTimestamp * TimeSpan.TicksPerMillisecond / 48);

        if (_mediaClock is null)
        {
            _renderer.Submit(pcm, pcmTimestamp);
            return;
        }

        var targetTicks = _mediaClock.AudioRtpToLocalWallTicks(frame.RtpTimestamp);
        if (targetTicks is null && _fallbackActive)
        {
            var deltaSamples = (long)(int)(frame.RtpTimestamp - _fallbackAnchorRtp);
            var deltaTicks = deltaSamples * Stopwatch.Frequency / 48_000L;
            targetTicks = _fallbackAnchorLocalTicks + deltaTicks;
        }
        if (targetTicks is null)
        {
            _renderer.Submit(pcm, pcmTimestamp);
            return;
        }

        var nowTicks = Stopwatch.GetTimestamp();
        var offsetMicros = (nowTicks - targetTicks.Value) * 1_000_000L / Stopwatch.Frequency;
        Interlocked.Exchange(ref _lastOffsetMicros, offsetMicros);

        var inSyncWindow = nowTicks < _nextSyncCheckTicks;
        var withinTolerance = offsetMicros >= -SyncToleranceMicros
                           && offsetMicros <= SyncToleranceMicros;

        if (inSyncWindow && withinTolerance)
        {
            // Steady-state: just play. No correction, no glitches.
            _renderer.Submit(pcm, pcmTimestamp);
            return;
        }

        // Either we're at a re-check tick OR we've drifted out of
        // tolerance. Re-align now.
        var sampleRate = frame.SampleRate > 0 ? frame.SampleRate : MediaClock.AudioRtpClockRate;
        var channels = frame.Channels > 0 ? frame.Channels : 2;

        if (offsetMicros > SyncToleranceMicros)
        {
            // Audio is late beyond tolerance. Drop this frame; drain
            // will pop the next one until the front of the queue is
            // close enough to "now" to play in sync.
            Interlocked.Add(ref _samplesSkippedTotal, frame.Samples);
            return;
        }

        if (offsetMicros < -SyncToleranceMicros)
        {
            // Audio is early beyond tolerance. Drain has already
            // gated us until target was within readyTolerance of
            // now, so being substantially early shouldn't normally
            // happen — the drain gate's tolerance is wider than this
            // tolerance only in rare cases (clock jump). Pad up to
            // 500 ms so we land back on the line, then submit.
            var aheadMicros = -offsetMicros;
            var gapMicrosCapped = Math.Min(aheadMicros, 500_000L);
            var silentFrames = (int)(gapMicrosCapped * sampleRate / 1_000_000L);
            if (silentFrames > 0)
            {
                var silentInterleaved = new short[silentFrames * channels];
                _renderer.Submit(silentInterleaved, pcmTimestamp);
                Interlocked.Add(ref _samplesPaddedTotal, silentFrames);
            }
            _renderer.Submit(pcm, pcmTimestamp);
            _nextSyncCheckTicks = nowTicks
                + (long)(SyncReCheckInterval.TotalSeconds * Stopwatch.Frequency);
            return;
        }

        // Within tolerance, just past a re-check tick: submit and
        // schedule the next check.
        _renderer.Submit(pcm, pcmTimestamp);
        _nextSyncCheckTicks = nowTicks
            + (long)(SyncReCheckInterval.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>
    /// RTP-timestamp comparison that respects 32-bit wrap. "older" means
    /// earlier in playout order; difference is interpreted modulo 2^32.
    /// </summary>
    private static bool IsOlderOrEqual(uint a, uint b)
    {
        return (int)(a - b) <= 0;
    }
}
