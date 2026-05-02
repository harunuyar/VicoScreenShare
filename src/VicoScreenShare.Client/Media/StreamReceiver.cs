namespace VicoScreenShare.Client.Media;

using System;
using System.Collections.Generic;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;

/// <summary>
/// Receiver-side counterpart to <see cref="CaptureStreamer"/>. Subscribes to an
/// <see cref="RTCPeerConnection"/>'s <c>OnVideoFrameReceived</c>, hands each
/// reassembled payload to an <see cref="IVideoDecoder"/>, and raises
/// <see cref="FrameArrived"/> with a <see cref="CaptureFrameData"/> that the
/// Phase 2 <c>WriteableBitmapRenderer</c> can render directly.
///
/// The concrete codec lives behind the <see cref="IVideoDecoderFactory"/>
/// passed to the constructor — default is VP8 via <see cref="VpxDecoderFactory"/>
/// so existing call sites keep compiling, and future codecs plug in without
/// touching this class.
/// </summary>
public sealed class StreamReceiver : ICaptureSource, IDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly IVideoDecoder _decoder;
    private readonly object _decodeLock = new();
    private DateTime _lastFrameUtc = DateTime.MinValue;
    private bool _attached;
    private bool _disposed;

    // Decode pipeline: the SIPSorcery receive thread must not block on decode
    // or renderer callbacks, because backgrounded WPF renderers get DWM-
    // throttled to ~1 FPS and would stall UDP intake for hundreds of ms, at
    // which point the kernel socket buffer overflows and packets drop. We
    // enqueue the encoded frame bytes onto a bounded channel, and a
    // dedicated worker thread drains the channel synchronously calling the
    // decoder. Bounded = 30 frames (~500 ms of video) — enough to tolerate
    // brief renderer stalls, short enough that we'd rather drop a stale
    // encoded frame than accumulate multi-second delay.
    private System.Threading.Channels.Channel<PendingEncodedFrame>? _decodeQueue;
    private System.Threading.Thread? _decodeWorker;
    private System.Threading.CancellationTokenSource? _decodeCts;
    private const int DecodeQueueCapacity = 30;
    // Diagnostic counters — the periodic pulse log reads and resets these.
    private long _decodeQueueDroppedTotal;
    private long _decoderExceptionsTotal;
    private long _decodeWorkerRanTotal;
    private long _skipToIdrEventsTotal;

    // PLI debounce. TryRequestKeyframe coalesces requests from every
    // trigger (decoder.KeyframeNeeded event, decode-worker exception,
    // queue-fill drop, output watchdog) so the SFU sees at most one
    // PLI per PliMinIntervalMs per receiver. Defense-in-depth alongside
    // the SFU's own per-publisher coalesce — if a noisy decoder were
    // raising KeyframeNeeded every frame at 144fps, this debounce caps
    // the PLI rate at the receiver before any network traffic happens.
    private readonly System.Diagnostics.Stopwatch _lastPliSentAt = new();
    private const int PliMinIntervalMs = 500;
    private long _pliRequestsSentTotal;
    private long _pliRequestsSuppressedTotal;

    // Output watchdog. Generic recovery trigger: codec-, decoder-, and
    // error-code-agnostic. Stamps _lastFrameOutputTicks every time a
    // decoded frame is delivered (CPU bytes or GPU texture). Stamps
    // _lastRtpArrivedTicks every time RTP arrives. A dedicated tick
    // (200 ms cadence) checks whether RTP has been flowing recently
    // (within OutputWatchdogActiveMs) AND no frame has come out for
    // longer than OutputWatchdogStaleMs — if so, fire TryRequestKeyframe
    // (which itself debounces, so this can't spam).
    //
    // This is the safety net for any decoder whose error-reporting
    // surface doesn't match its actual failure mode. NVDEC raises
    // KeyframeNeeded on every parser failure (rich error codes); MFT
    // decoders only raise on a couple of HRESULT paths and silently
    // consume bitstream the rest of the time when packet loss desyncs
    // them. The watchdog catches both the same way: by observation,
    // not by error inspection.
    private long _lastFrameOutputTicks;
    private long _lastRtpArrivedTicks;
    private System.Threading.Timer? _outputWatchdogTimer;
    private const int OutputWatchdogStaleMs = 500;
    private const int OutputWatchdogActiveMs = 1000;
    private const int OutputWatchdogTickMs = 200;

    // AV1 RTP reassembly state. SIPSorcery has no AV1 framer; we listen
    // to the raw OnRtpPacketReceived path, accumulate packets per RTP
    // timestamp, and run Av1RtpDepacketizer on the marker-bit packet.
    // The assembled OBU stream is enqueued onto _decodeQueue same as
    // any other codec — the decode worker doesn't care about the codec.
    //
    // Packets are buffered with their RTP sequence number and sorted on
    // marker-bit so the depacketizer always sees them in transmit order.
    // RFC 9066's Z/Y fragmentation bookkeeping only makes sense in seq
    // order — feeding out-of-order packets produces malformed OBUs that
    // the MFT decoder silently rejects, then the decoder spends the rest
    // of the intra-refresh period (~1 s) putting out garbage / nothing
    // until the next refresh wave heals the reference chain. Keyframes
    // are ~200 RTP packets at 28 Mbps and so far more vulnerable to UDP
    // reordering than tiny P-frames, which matches the observed symptom
    // of losing keyframes specifically.
    private readonly bool _isAv1;
    private readonly List<(ushort Seq, byte[] Payload)> _av1PacketBuffer = new();
    // Reusable scratch list for handing payloads in transmit order
    // to Av1RtpDepacketizer. Cleared and refilled per frame instead
    // of allocated per frame — at 144 fps on a single subscriber
    // that's ~1 List<byte[]> + a backing array allocation saved per
    // frame (small, but it adds up across all the per-frame list
    // allocations in this hot path).
    private readonly List<byte[]> _av1OrderedPayloadsScratch = new();
    private uint _av1CurrentTimestamp;
    private bool _av1HasFramePackets;

    private long _lastPulseReceived;
    private long _lastPulseBytes;
    private long _lastPulseDropped;
    private long _lastPulseExceptions;
    private long _lastPulseWorkerRan;
    private long _lastPulseSkipToIdr;
    private System.Threading.Timer? _pulseTimer;

    // Drop-to-IDR recovery state. When the decode queue fills, we drain
    // it and reject incoming non-keyframe input until we see an IDR, so
    // the decoder never consumes a P-frame whose reference chain we
    // broke. Without this the decoder produces color-smear corruption
    // that persists until the next natural IDR anyway — skipping to
    // the IDR directly is cleaner UX (brief freeze, then clean video).
    // A framecount guard prevents hanging on intra-refresh streams that
    // never emit a classical IDR.
    private bool _awaitingKeyframe;
    private int _awaitingKeyframeFramesSeen;
    private const int AwaitingKeyframeMaxFrames = 240; // ~2 s at 120 fps
    private uint _remoteSsrc;

    // Last RTP timestamp we successfully assembled and enqueued for AV1.
    // SIPSorcery's RTP layer occasionally delivers a retransmitted (or
    // duplicated by a NACK round-trip) marker-bit packet for a frame
    // whose other packets we already saw — without dedup, our reassembly
    // produces the SAME OBU stream twice and feeds it to the MFT decoder
    // twice. The MFT rejects the duplicate as MF_E_INVALID_STREAM_DATA
    // (0xC00D36CB) and from then on returns failure on every subsequent
    // ProcessOutput. uint == is wrap-safe for "same value" checks.
    private uint _av1LastEnqueuedTimestamp;
    private bool _av1HasEnqueued;

    // Receive-boundary PTS-gap probe. Logs whenever the RTP timestamp
    // delta between successive arriving frames exceeds the threshold.
    // Lets us tell apart "publisher's capture cadence dipped" (wire
    // already had this gap) vs "PaintLoop scheduling logic invented the
    // gap on the receive side". Wall-time stamp lets us see whether the
    // gap also corresponds to a wall-time idle window.
    private uint _lastArrivalRtpTs;
    private long _lastArrivalWallTicks;
    private bool _hasArrivalSample;
    private const double ArrivalGapLogThresholdMs = 25.0;

    /// <summary>
    /// Pending decode work item. <see cref="Length"/> is the valid byte
    /// count inside <see cref="Bytes"/> — usually equal to
    /// <c>Bytes.Length</c> for the SIPSorcery codec path, but can be
    /// less when <see cref="Bytes"/> is rented from
    /// <see cref="System.Buffers.ArrayPool{T}.Shared"/>. When
    /// <see cref="Pooled"/> is true the consumer MUST call
    /// <see cref="System.Buffers.ArrayPool{T}.Return"/> on
    /// <see cref="Bytes"/> when finished.
    /// </summary>
    private readonly record struct PendingEncodedFrame(
        byte[] Bytes, int Length, uint RtpTimestamp, DateTime ArrivalUtc, bool Pooled);

    // RFC 3550 §A.3 sequence-stats for this receiver's incoming video RTP.
    // Derived loss, reorder-aware — see RtpSequenceStats for the math.
    private readonly RtpSequenceStats _rtpStats = new();
    private uint _lastLoggedSsrc;

    private readonly MediaClock? _mediaClock;

    public StreamReceiver(RTCPeerConnection pc, string displayName = "remote")
        : this(pc, new VpxDecoderFactory(), displayName, mediaClock: null)
    {
    }

    public StreamReceiver(RTCPeerConnection pc, IVideoDecoderFactory decoderFactory, string displayName = "remote")
        : this(pc, decoderFactory, displayName, mediaClock: null)
    {
    }

    public StreamReceiver(
        RTCPeerConnection pc,
        IVideoDecoderFactory decoderFactory,
        string displayName,
        MediaClock? mediaClock)
        : this(pc, decoderFactory, displayName, mediaClock, videoWidth: 0, videoHeight: 0)
    {
    }

    public StreamReceiver(
        RTCPeerConnection pc,
        IVideoDecoderFactory decoderFactory,
        string displayName,
        MediaClock? mediaClock,
        int videoWidth,
        int videoHeight)
    {
        _pc = pc;
        DisplayName = displayName;
        _mediaClock = mediaClock;
        // Use the dimensioned factory overload when the publisher's
        // StreamStarted message included real dimensions. The MS AV1
        // MFT pre-negotiates at this size and skips the STREAM_CHANGE
        // round-trip on the first IDR; other decoders' default impls
        // ignore the hint and fall back to the no-arg construct.
        DebugLog.Write($"[recv {DisplayName}] decoder-ctor entry — factory={decoderFactory.GetType().Name} codec={decoderFactory.Codec} hint={videoWidth}x{videoHeight}");
        try
        {
            _decoder = (videoWidth > 0 && videoHeight > 0)
                ? decoderFactory.CreateDecoder(videoWidth, videoHeight)
                : decoderFactory.CreateDecoder();
            DebugLog.Write($"[recv {DisplayName}] decoder-ctor done — type={_decoder.GetType().Name} codec={_decoder.Codec}");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[recv {DisplayName}] decoder-ctor threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
        _isAv1 = _decoder.Codec == VideoCodec.Av1;
        // Opt the decoder into GPU-resident output. Decoders that don't
        // support it (VPX, and MF decoders with no shared D3D device) use
        // the default no-op setter; their frames still flow via the CPU
        // byte[] path. MF + shared device emits here, which saves the
        // per-frame BGRA readback + upload round-trip.
        _decoder.GpuOutputHandler = OnDecoderGpuFrame;
        // Listen for decoder-side recovery hints. Decoders raise this
        // when their internal state diverges (NVDEC parser desync, MFT
        // TYPE_NOT_SET wedge, libvpx exception) — we respond by sending
        // RTCP PLI upstream so the publisher emits a fresh IDR. Default
        // add/remove on IVideoDecoder is empty, so this is safe to
        // subscribe unconditionally on any decoder.
        _decoder.KeyframeNeeded += OnDecoderRequestedKeyframe;
    }

    private void OnDecoderRequestedKeyframe()
    {
        TryRequestKeyframe("decoder requested");
    }

    /// <summary>
    /// Shared A/V sync clock for this receiver's session, when the
    /// owning <see cref="Services.SubscriberSession"/> built one.
    /// The video renderer reads this through and latches the anchor
    /// at the first-paint moment — that's what ties audio playout to
    /// video's effective wall-clock schedule, regardless of any
    /// buffering or delay between the network and the screen.
    /// </summary>
    public MediaClock? MediaClock => _mediaClock;

    public string DisplayName { get; }

    private long _framesReceivedCount;
    private long _encodedByteCountAtomic;

    /// <summary>
    /// Count of complete encoded frames that have arrived on this
    /// receiver's video track. Incremented at network arrival (on the
    /// SIPSorcery receive thread), so it reflects what the wire
    /// actually delivered — not what survived the decode queue.
    /// </summary>
    public long FramesReceived => System.Threading.Interlocked.Read(ref _framesReceivedCount);

    public long FramesDecoded { get; private set; }

    /// <summary>
    /// Cumulative encoded bytes received on the peer connection's video
    /// track since this receiver attached. Measured at arrival, so the
    /// stats overlay's derived bitrate reflects real network intake even
    /// when the decode queue is shedding frames to keep up.
    /// </summary>
    public long EncodedByteCount => System.Threading.Interlocked.Read(ref _encodedByteCountAtomic);

    /// <summary>
    /// Cumulative count of encoded frames dropped before they reached the
    /// decoder — either evicted from the bounded decode queue under
    /// backpressure, or discarded by the drop-to-IDR gate while waiting
    /// for a keyframe. Non-zero values signal the receiver is losing
    /// source frames (visible as reduced paint fps / frozen tile).
    /// </summary>
    public long DecodeQueueDroppedCount => System.Threading.Interlocked.Read(ref _decodeQueueDroppedTotal);

    /// <summary>
    /// Cumulative count of drop-to-IDR events: times we drained the
    /// decode queue and armed the await-keyframe gate because the decoder
    /// couldn't keep up. Each event costs up to one keyframe interval of
    /// visible freeze. A counter stuck at zero means the decoder is
    /// comfortably keeping up; a rising counter means you're at or past
    /// the decode capacity limit for this machine.
    /// </summary>
    public long SkipToIdrCount => System.Threading.Interlocked.Read(ref _skipToIdrEventsTotal);

    /// <summary>Width of the most recently decoded frame, or 0 if nothing
    /// has decoded yet.</summary>
    public int LastWidth { get; private set; }

    /// <summary>Same for height.</summary>
    public int LastHeight { get; private set; }

    /// <summary>Codec tag for the decoder instance powering this receiver.</summary>
    public VideoCodec Codec => _decoder.Codec;

    /// <summary>Total RTP video packets seen, including reorders.</summary>
    public long RtpPacketsReceived => _rtpStats.Received;

    /// <summary>Reorder-aware inferred loss (see <see cref="RtpSequenceStats"/>).</summary>
    public long RtpPacketsInferredLost => _rtpStats.Lost;

    /// <summary>
    /// Loss% from the local packet observer, independent of the RTCP RR
    /// chain. 0..100.
    /// </summary>
    public double RtpLossPercent => _rtpStats.LossPercent;

    public event FrameArrivedHandler? FrameArrived;

    /// <summary>
    /// Raised synchronously from the decoder thread when a GPU-capable
    /// decoder emits a BGRA <c>ID3D11Texture2D</c> on the shared device.
    /// The native pointer is valid ONLY for the duration of the call;
    /// subscribers must <c>CopyResource</c> (or otherwise consume) before
    /// returning. When this fires, <see cref="FrameArrived"/> does NOT
    /// fire for the same frame — the decoder already skipped the CPU
    /// readback. For decoders without GPU support (VPX, sysmem MF)
    /// frames continue to arrive on <see cref="FrameArrived"/> as before.
    /// </summary>
    public event TextureArrivedHandler? TextureArrived;

    /// <summary>Alias for <see cref="FrameArrived"/> that reads clearer from the receiver side.</summary>
    public event FrameArrivedHandler? FrameDecoded
    {
        add => FrameArrived += value;
        remove => FrameArrived -= value;
    }

    /// <summary>
    /// Terminal state — <see cref="RTCPeerConnectionState.closed"/> or
    /// <see cref="RTCPeerConnectionState.failed"/>. The peer connection
    /// will not recover; callers should tear the receiver down.
    /// </summary>
    public event Action? Closed;

    /// <summary>
    /// Transient state — <see cref="RTCPeerConnectionState.disconnected"/>.
    /// ICE has stopped exchanging keepalives but the connection may still
    /// recover. Pairs with <see cref="Reconnected"/>.
    /// </summary>
    public event Action? Disconnected;

    /// <summary>
    /// Fires when ICE returns to <see cref="RTCPeerConnectionState.connected"/>.
    /// Lets callers clear any "Reconnecting…" UI shown after a previous
    /// <see cref="Disconnected"/>.
    /// </summary>
    public event Action? Reconnected;

    public System.Threading.Tasks.Task StartAsync()
    {
        if (_attached || _disposed)
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        _attached = true;

        // Spin up the decode worker BEFORE attaching the SIPSorcery event,
        // otherwise the first OnVideoFrameReceived could find an unready
        // queue and get dropped.
        var opts = new System.Threading.Channels.BoundedChannelOptions(30)
        {
            // Drop the oldest queued encoded frame when the worker can't
            // keep up. We'd rather lose a stale frame than let backpressure
            // reach the receive thread and cause actual UDP packet loss.
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        };
        _decodeQueue = System.Threading.Channels.Channel.CreateBounded<PendingEncodedFrame>(opts);
        _decodeCts = new System.Threading.CancellationTokenSource();
        _decodeWorker = new System.Threading.Thread(DecodeWorkerLoop)
        {
            IsBackground = true,
            Name = $"rx-decode {DisplayName}",
            // AboveNormal so background WPF apps don't starve the decode
            // loop when their UI thread is lower priority. This is the
            // thread that does the actual H.264 decode + renderer invoke,
            // and we want it to get CPU time regardless of window focus.
            Priority = System.Threading.ThreadPriority.AboveNormal,
        };
        _decodeWorker.Start();

        _pc.OnVideoFrameReceived += OnVideoFrameReceived;
        _pc.OnRtpPacketReceived += OnRtpPacketReceived;
        _pc.onconnectionstatechange += OnConnectionStateChange;

        // Pulse log every 2 s. Rate-limited and cheap — a handful of
        // Interlocked reads + one DebugLog line per subscriber. Gives
        // us a window into exactly where frames are being dropped:
        // decode queue saturation vs decoder exceptions vs clean flow.
        _pulseTimer = new System.Threading.Timer(
            _ => EmitPulse(), null,
            System.TimeSpan.FromSeconds(2),
            System.TimeSpan.FromSeconds(2));

        // Output-stagnation watchdog. Runs at OutputWatchdogTickMs cadence
        // independent of the 2s pulse so detection latency is bounded by
        // the tick interval, not by when EmitPulse next fires.
        _outputWatchdogTimer = new System.Threading.Timer(
            _ => CheckOutputWatchdog(), null,
            System.TimeSpan.FromMilliseconds(OutputWatchdogTickMs),
            System.TimeSpan.FromMilliseconds(OutputWatchdogTickMs));
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void CheckOutputWatchdog()
    {
        if (_disposed)
        {
            return;
        }
        // Need recent RTP traffic. If the publisher stopped sending,
        // the decoder having no output is correct, not a recovery
        // condition.
        var rtpStamp = _lastRtpArrivedTicks;
        if (rtpStamp == 0)
        {
            return;
        }
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var rtpAgeMs = (nowTicks - rtpStamp) * 1000.0 / freq;
        if (rtpAgeMs > OutputWatchdogActiveMs)
        {
            return;
        }
        // RTP is fresh. Check output staleness. _lastFrameOutputTicks=0
        // means we've never produced a frame yet — that's the "first
        // frame missing" case, also worth a PLI: the publisher's IDR
        // either was lost or hasn't been emitted, and PLI is the
        // standard way to ask for one.
        var outStamp = _lastFrameOutputTicks;
        if (outStamp == 0)
        {
            TryRequestKeyframe($"watchdog (no output yet, rtpAge={rtpAgeMs:F0}ms)");
            return;
        }
        var outputAgeMs = (nowTicks - outStamp) * 1000.0 / freq;
        if (outputAgeMs > OutputWatchdogStaleMs)
        {
            TryRequestKeyframe($"watchdog ({outputAgeMs:F0}ms since last frame, rtpAge={rtpAgeMs:F0}ms)");
        }
    }

    private void EmitPulse()
    {
        if (_disposed)
        {
            return;
        }
        var received = System.Threading.Interlocked.Read(ref _framesReceivedCount);
        var bytes = System.Threading.Interlocked.Read(ref _encodedByteCountAtomic);
        var dropped = System.Threading.Interlocked.Read(ref _decodeQueueDroppedTotal);
        var exceptions = System.Threading.Interlocked.Read(ref _decoderExceptionsTotal);
        var workerRan = System.Threading.Interlocked.Read(ref _decodeWorkerRanTotal);
        var skipEvents = System.Threading.Interlocked.Read(ref _skipToIdrEventsTotal);
        var qCount = _decodeQueue?.Reader.Count ?? 0;

        var dR = received - _lastPulseReceived; _lastPulseReceived = received;
        var dB = bytes - _lastPulseBytes; _lastPulseBytes = bytes;
        var dDrop = dropped - _lastPulseDropped; _lastPulseDropped = dropped;
        var dExc = exceptions - _lastPulseExceptions; _lastPulseExceptions = exceptions;
        var dWr = workerRan - _lastPulseWorkerRan; _lastPulseWorkerRan = workerRan;
        var dSkip = skipEvents - _lastPulseSkipToIdr; _lastPulseSkipToIdr = skipEvents;

        // Quiet when idle (no frames in the window) so stopped rooms
        // don't spam the log.
        if (dR == 0 && dWr == 0)
        {
            return;
        }

        var mbps = dB * 8.0 / 2.0 / 1_000_000.0;
        var pliSent = System.Threading.Interlocked.Read(ref _pliRequestsSentTotal);
        var pliSuppressed = System.Threading.Interlocked.Read(ref _pliRequestsSuppressedTotal);
        DebugLog.Write(
            $"[recv-pulse {DisplayName}] 2s: rcvd=+{dR} dec=+{dWr} dropped=+{dDrop} excep=+{dExc} " +
            $"skip2idr=+{dSkip} decQ={qCount}/{DecodeQueueCapacity} bw={mbps:F1} Mbps " +
            $"pli={pliSent}/{pliSent + pliSuppressed}");

    }

    private void DecodeWorkerLoop()
    {
        var reader = _decodeQueue!.Reader;
        var token = _decodeCts!.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                PendingEncodedFrame pending;
                try
                {
                    pending = reader.ReadAsync(token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                DecodeAndDispatch(pending);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[recv {DisplayName}] decode worker threw: {ex.GetType().Name}: {ex.Message}");
        }
    }


    public System.Threading.Tasks.Task StopAsync()
    {
        if (!_attached)
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        _attached = false;
        _pc.OnVideoFrameReceived -= OnVideoFrameReceived;
        _pc.OnRtpPacketReceived -= OnRtpPacketReceived;
        _pc.onconnectionstatechange -= OnConnectionStateChange;
        try { _pulseTimer?.Dispose(); } catch { }
        _pulseTimer = null;
        try { _outputWatchdogTimer?.Dispose(); } catch { }
        _outputWatchdogTimer = null;

        // Stop the decode worker. Cancel first to unblock any pending
        // ReadAsync, complete the channel so the loop exits cleanly, then
        // join with a short timeout so Dispose doesn't hang if the worker
        // is stuck in native decode code.
        try { _decodeCts?.Cancel(); } catch { }
        try { _decodeQueue?.Writer.TryComplete(); } catch { }
        try { _decodeWorker?.Join(TimeSpan.FromMilliseconds(500)); } catch { }
        try { _decodeCts?.Dispose(); } catch { }
        _decodeCts = null;
        _decodeWorker = null;
        _decodeQueue = null;
        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// Raw RTP packet observer — maintains the sequence-gap loss counters
    /// used by <see cref="RtpLossPercent"/> and, for AV1 streams,
    /// reassembles RTP payloads into OBU temporal units via
    /// <see cref="Av1RtpDepacketizer"/>. Runs on SIPSorcery's receive
    /// thread; keep this path branch-light and allocation-free on the
    /// non-AV1 fast path.
    /// </summary>
    private long _lastRtpPacketTicks;
    private long _rtpInterPacketSpikeCount;
    private long _rtpProcessingSpikeCount;

    private void OnRtpPacketReceived(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
    {
        if (mediaType != SDPMediaTypesEnum.video || packet is null)
        {
            return;
        }

        // Watchdog input #1: most-recent RTP arrival. The output-stagnation
        // tick uses this to know whether RTP is still flowing — if it isn't,
        // a stagnant decoder isn't a recovery problem (no new bitstream
        // means nothing for the decoder to do).
        _lastRtpArrivedTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        // Layer 1 probe: inter-packet arrival gap on the SIPSorcery
        // RTP receive thread. Healthy traffic at 25 Mbps / 144 fps
        // delivers an RTP packet every ~50-200 µs (1-2 packets per
        // frame at MTU-sized payloads, frames every 7 ms). A gap > 30
        // ms means the receive thread (or kernel UDP buffer drain)
        // stalled — this is the upstream-most layer we can probe.
        var nowTicksRtp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastRtpPacketTicks != 0)
        {
            var gapMs = (nowTicksRtp - _lastRtpPacketTicks) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
            if (gapMs > 30.0)
            {
                var n = ++_rtpInterPacketSpikeCount;
                if (n <= 200 || n % 50 == 0)
                {
                    DebugLog.Write($"[recv-l1-rtp-gap {DisplayName}] gap={gapMs:F1}ms seq={packet.Header.SequenceNumber} marker={packet.Header.MarkerBit}");
                }
            }
        }
        _lastRtpPacketTicks = nowTicksRtp;

        var ssrc = packet.Header.SyncSource;
        var seq = packet.Header.SequenceNumber;

        if (_lastLoggedSsrc == 0)
        {
            DebugLog.Write($"[recv {DisplayName}] FIRST RTP packet ssrc=0x{ssrc:X8} seq={seq} pt={packet.Header.PayloadType} size={packet.Payload?.Length ?? 0} isAv1={_isAv1}");
        }
        else if (_lastLoggedSsrc != ssrc)
        {
            DebugLog.Write($"[recv {DisplayName}] SSRC changed 0x{_lastLoggedSsrc:X8} -> 0x{ssrc:X8} at seq {seq}");
        }
        _lastLoggedSsrc = ssrc;
        _remoteSsrc = ssrc;

        _rtpStats.Observe(ssrc, seq);

        if (!_isAv1)
        {
            return;
        }

        // AV1 reassembly. RFC 9066 says the marker bit is set on the last
        // packet of a temporal unit. Group by RTP timestamp; if the
        // timestamp changes mid-flight we treat the previous group as
        // dropped (incomplete) and start over — happens when the marker-
        // bit packet itself was lost.
        var ts = packet.Header.Timestamp;
        if (_av1HasFramePackets && ts != _av1CurrentTimestamp)
        {
            // Marker-bit packet was lost: discard the prior frame's
            // partial packets so we don't concatenate them into the
            // next frame's reassembly. Log when this happens because
            // it means the receiver missed a marker bit — visible to
            // the user as one stuttered frame.
            if (_av1PacketBuffer.Count > 0)
            {
                DebugLog.Write($"[av1-rx-discard] missing-marker discard: dropping {_av1PacketBuffer.Count} packets at ts={_av1CurrentTimestamp} (new ts={ts})");
            }
            _av1PacketBuffer.Clear();
            _av1HasFramePackets = false;
        }
        var payload = packet.Payload;
        if (payload is null)
        {
            return;
        }
        _av1CurrentTimestamp = ts;
        _av1HasFramePackets = true;
        _av1PacketBuffer.Add((seq, payload));

        if (packet.Header.MarkerBit == 1)
        {
            // Sort by RTP sequence number with a signed-short comparator
            // so a frame whose packets cross the 65535 → 0 wraparound
            // still sorts correctly. All packets of one AV1 temporal unit
            // share the same RTP timestamp and have consecutive sequence
            // numbers assigned in transmit order, so this restores
            // transmit order regardless of UDP reordering.
            _av1PacketBuffer.Sort((a, b) => (short)(a.Seq - b.Seq));

            // Intra-frame packet-loss detection. RFC 9066 says all packets
            // of one temporal unit have contiguous sequence numbers in
            // transmit order. After sorting, count must equal the
            // (lastSeq - firstSeq + 1) span. A mismatch means at least one
            // packet was lost in the kernel/wire — the OBU stream we'd
            // assemble would be malformed (mid-OBU fragments concatenated
            // without their middle chunks), and the MFT decoder would
            // reject the whole sample with HRESULT 0xC00D36CB and stay
            // stuck until the next IDR. Drop the frame on detection: we
            // can't reconstruct it, but we keep the decoder healthy for
            // the next IDR. This is the most common failure on screen-
            // share IDRs at 4K because a 600+ KB IDR bursts ~500 packets
            // through Windows' default 64 KB UDP receive buffer.
            var firstSeq = _av1PacketBuffer[0].Seq;
            var lastSeq = _av1PacketBuffer[_av1PacketBuffer.Count - 1].Seq;
            var expectedCount = (ushort)(lastSeq - firstSeq + 1);
            if (expectedCount != _av1PacketBuffer.Count)
            {
                DebugLog.Write($"[av1-rx-loss] intra-frame gap, dropping frame: count={_av1PacketBuffer.Count} expectedSpan={expectedCount} firstSeq={firstSeq} lastSeq={lastSeq} ts={ts}");
                _av1PacketBuffer.Clear();
                _av1HasFramePackets = false;
                return;
            }

            if (_av1PacketBuffer.Count > 50)
            {
                DebugLog.Write($"[av1-rx-bigframe] marker fired with {_av1PacketBuffer.Count} packets ts={ts} firstSeq={firstSeq} lastSeq={lastSeq}");
            }

            _av1OrderedPayloadsScratch.Clear();
            if (_av1OrderedPayloadsScratch.Capacity < _av1PacketBuffer.Count)
            {
                _av1OrderedPayloadsScratch.Capacity = _av1PacketBuffer.Count;
            }
            foreach (var (_, p) in _av1PacketBuffer)
            {
                _av1OrderedPayloadsScratch.Add(p);
            }
            // DepacketizePooled rents the assembled OBU stream from
            // ArrayPool<byte>.Shared, sidestepping the per-IDR LOH
            // allocation that previously triggered Gen2 GCs and
            // false-positive watchdog flushes.
            var (assembled, assembledLength) =
                Av1RtpDepacketizer.DepacketizePooled(_av1OrderedPayloadsScratch);
            _av1PacketBuffer.Clear();
            _av1HasFramePackets = false;
            if (assembled is null || assembledLength == 0)
            {
                if (assembled is not null)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(assembled);
                }
                return;
            }

            // Proof-driven probe: enumerate the OBU types in the
            // assembled stream and log them when the frame is large
            // (these are the ones the MFT decoder rejects with
            // 0xC00D36CB). A valid AV1 keyframe temporal unit MUST
            // contain at least: OBU_TEMPORAL_DELIMITER (type 2),
            // OBU_SEQUENCE_HEADER (type 1), OBU_FRAME_HEADER (3) +
            // OBU_TILE_GROUP (4), OR a fused OBU_FRAME (type 6).
            // Missing types reveal whether NVENC is shipping the
            // expected layout or whether our receiver is stripping
            // something it shouldn't.
            if (assembledLength > 100_000)
            {
                LogAv1ObuBreakdown(assembled, assembledLength, ts);
            }
            // Drop a duplicate of an already-decoded frame. See the
            // _av1LastEnqueuedTimestamp field comment.
            if (_av1HasEnqueued && ts == _av1LastEnqueuedTimestamp)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(assembled);
                return;
            }
            _av1LastEnqueuedTimestamp = ts;
            _av1HasEnqueued = true;
            ProbeArrivalGap(ts);
            EnqueueDecodeFrame(assembled, assembledLength, ts, obuStreamPooled: true);

            // Layer 2 probe: total time spent inside this RTP-receive
            // call when it ended up doing AV1 reassembly + enqueue.
            // Includes: sort, gap detection, OBU breakdown, depacketize
            // (with pool rent), enqueue. SIPSorcery is on the same
            // thread as the next packet, so a slow run here delays
            // every subsequent packet behind it.
            var rtpProcMs = (System.Diagnostics.Stopwatch.GetTimestamp() - nowTicksRtp) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
            if (rtpProcMs > 5.0)
            {
                var n = ++_rtpProcessingSpikeCount;
                if (n <= 200 || n % 50 == 0)
                {
                    DebugLog.Write($"[recv-l2-assemble {DisplayName}] proc={rtpProcMs:F1}ms ts={ts} bytes={assembledLength}");
                }
            }
        }
    }

    /// <summary>
    /// Walks the assembled AV1 OBU stream the depacketizer just
    /// produced and logs a one-line breakdown: counts of each OBU
    /// type plus the total length. Lets us see at a glance whether
    /// large keyframes have the [TD][SeqHdr][Frame] structure the
    /// MFT decoder requires.
    /// </summary>
    private static void LogAv1ObuBreakdown(byte[] obuStream, int validLength, uint rtpTs)
    {
        // OBU types per AV1 spec 6.2.1: 1=SEQ_HDR, 2=TD, 3=FRAME_HDR,
        // 4=TILE_GROUP, 5=METADATA, 6=FRAME, 7=REDUNDANT_FRAME_HDR,
        // 15=PADDING. Histogram each type seen. obu_has_size_field
        // is set on every OBU (we just rebuilt the stream that way).
        var counts = new int[16];
        var pos = 0;
        var totalParsed = 0;
        var firstType = -1;
        while (pos < validLength)
        {
            var header = obuStream[pos];
            var obuType = (header >> 3) & 0xF;
            if (firstType < 0)
            {
                firstType = obuType;
            }
            counts[obuType]++;
            var hasExtension = (header & 0x04) != 0;
            var hasSize = (header & 0x02) != 0;
            var hl = 1 + (hasExtension ? 1 : 0);
            if (pos + hl > validLength)
            {
                break;
            }
            int payloadSize;
            if (hasSize)
            {
                var (size, lebBytes) = Av1RtpPacketizer.ReadLeb128(obuStream, pos + hl);
                payloadSize = (int)size;
                hl += lebBytes;
            }
            else
            {
                payloadSize = validLength - (pos + hl);
            }
            pos += hl + payloadSize;
            totalParsed++;
            if (totalParsed > 200) { break; }
        }
        DebugLog.Write($"[av1-obu] ts={rtpTs} bytes={validLength} obuCount={totalParsed} firstType={firstType} TD={counts[2]} SeqHdr={counts[1]} FrameHdr={counts[3]} TileGroup={counts[4]} Frame={counts[6]} Metadata={counts[5]} RedundantHdr={counts[7]} Padding={counts[15]}");
    }

    /// <summary>
    /// Logs the RTP-timestamp delta and the wall-time delta between the
    /// previous arriving frame and this one when the gap exceeds
    /// <see cref="ArrivalGapLogThresholdMs"/>. RTP timestamps are 90 kHz,
    /// so a 7 ms steady-state cadence at 144 fps reads as ~625 ticks
    /// between successive frames. Logs when the delta crosses 25 ms,
    /// which is well above normal jitter and below the user-perceptible
    /// "stutter" threshold. Comparing the two deltas tells us whether
    /// the gap was already on the wire (publisher capture cadence
    /// dipped, both deltas large) or was introduced after receive
    /// (RTP delta small, wall delta large = transport jitter).
    /// </summary>
    private void ProbeArrivalGap(uint rtpTs)
    {
        var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_hasArrivalSample)
        {
            _lastArrivalRtpTs = rtpTs;
            _lastArrivalWallTicks = nowTicks;
            _hasArrivalSample = true;
            return;
        }

        // Signed subtraction handles uint wrap correctly.
        var rtpDelta = unchecked((int)(rtpTs - _lastArrivalRtpTs));
        var rtpDeltaMs = rtpDelta / 90.0;
        var wallDeltaMs = (nowTicks - _lastArrivalWallTicks) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;

        _lastArrivalRtpTs = rtpTs;
        _lastArrivalWallTicks = nowTicks;

        var biggest = Math.Max(Math.Abs(rtpDeltaMs), wallDeltaMs);
        if (biggest > ArrivalGapLogThresholdMs)
        {
            DebugLog.Write(
                $"[recv-gap {DisplayName}] rtpDelta={rtpDeltaMs:F1}ms wallDelta={wallDeltaMs:F1}ms");
        }
    }

    /// <summary>
    /// Enqueue an assembled AV1 OBU stream onto the decode queue, mirroring
    /// the path <see cref="OnVideoFrameReceived"/> uses for SIPSorcery-
    /// framed codecs. Same drop-to-IDR backpressure logic applies; AV1
    /// keyframe detection looks for OBU_SEQUENCE_HEADER anywhere in the
    /// assembled stream.
    /// </summary>
    private void EnqueueDecodeFrame(byte[] obuStream, int obuStreamLength, uint timestamp, bool obuStreamPooled)
    {
        if (_disposed)
        {
            if (obuStreamPooled && obuStream is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(obuStream);
            }
            return;
        }
        System.Threading.Interlocked.Increment(ref _framesReceivedCount);
        System.Threading.Interlocked.Add(ref _encodedByteCountAtomic, obuStreamLength);

        var queue = _decodeQueue;
        if (queue is null)
        {
            if (obuStreamPooled)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(obuStream);
            }
            return;
        }

        var isKey = Av1RtpPacketizer.ContainsSequenceHeader(obuStream, obuStreamLength);

        if (_awaitingKeyframe)
        {
            if (isKey)
            {
                _awaitingKeyframe = false;
                _awaitingKeyframeFramesSeen = 0;
            }
            else
            {
                _awaitingKeyframeFramesSeen++;
                if (_awaitingKeyframeFramesSeen < AwaitingKeyframeMaxFrames)
                {
                    System.Threading.Interlocked.Increment(ref _decodeQueueDroppedTotal);
                    if (obuStreamPooled)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(obuStream);
                    }
                    return;
                }
                _awaitingKeyframe = false;
                _awaitingKeyframeFramesSeen = 0;
            }
        }

        if (queue.Reader.Count >= DecodeQueueCapacity - 1)
        {
            DrainDecodeQueueLocked(queue);
            System.Threading.Interlocked.Increment(ref _skipToIdrEventsTotal);
            System.Threading.Interlocked.Increment(ref _decodeQueueDroppedTotal);
            TryRequestKeyframe("decode-queue-drain");
            if (!isKey)
            {
                _awaitingKeyframe = true;
                _awaitingKeyframeFramesSeen = 0;
                if (obuStreamPooled)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(obuStream);
                }
                return;
            }
        }

        if (!queue.Writer.TryWrite(new PendingEncodedFrame(obuStream, obuStreamLength, timestamp, DateTime.UtcNow, obuStreamPooled)))
        {
            // TryWrite false on bounded channel under FullMode.DropWrite
            // means the channel rejected our item without drop-policy
            // running; ensure we still return the rented buffer.
            if (obuStreamPooled)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(obuStream);
            }
        }
    }

    public async System.Threading.Tasks.ValueTask DisposeAsync()
    {
        // Fully async to avoid sync-over-async deadlocks when called from
        // the UI thread (e.g. RoomViewModel's leave-room teardown). The
        // previous Dispose-then-block-on-StopAsync pattern wedged the UI
        // when the decode worker's tear-down posted continuations back
        // to the UI thread that we were blocking.
        try { await StopAsync().ConfigureAwait(false); } catch { }
        lock (_decodeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { _decoder.KeyframeNeeded -= OnDecoderRequestedKeyframe; } catch { }
            try { _decoder.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        // Synchronous fallback for IDisposable contracts. Best-effort —
        // callers should prefer DisposeAsync. We still avoid blocking on
        // StopAsync's worker-thread join since StopAsync only does cheap
        // signal-based teardown (CancellationToken cancel + channel
        // complete + bounded Join with timeout).
        try { StopAsync().ConfigureAwait(false).GetAwaiter().GetResult(); } catch { }
        lock (_decodeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { _decoder.KeyframeNeeded -= OnDecoderRequestedKeyframe; } catch { }
            try { _decoder.Dispose(); } catch { }
        }
    }

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        switch (state)
        {
            case RTCPeerConnectionState.disconnected:
                Disconnected?.Invoke();
                break;
            case RTCPeerConnectionState.connected:
                Reconnected?.Invoke();
                break;
            case RTCPeerConnectionState.closed:
            case RTCPeerConnectionState.failed:
                Closed?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Called on the SIPSorcery UDP receive thread. MUST return quickly —
    /// this path runs on the same thread that drains the kernel socket
    /// buffer, so any block here risks packet loss to buffer overflow.
    /// All we do is stamp the frame and hand it to the decode worker; real
    /// decode + renderer invocation happens on the dedicated worker thread.
    /// </summary>
    private void OnVideoFrameReceived(IPEndPoint remote, uint timestamp, byte[] encodedSample, VideoFormat format)
    {
        if (encodedSample is null || encodedSample.Length == 0 || _disposed)
        {
            return;
        }

        if (System.Threading.Interlocked.Read(ref _framesReceivedCount) == 0)
        {
            DebugLog.Write($"[recv {DisplayName}] FIRST framed sample size={encodedSample.Length} pt={format.FormatID} fmt={format.FormatName} isAv1={_isAv1}");
        }

        ProbeArrivalGap(timestamp);

        // For AV1 the wire PT is borrowed from H.264 (SIPSorcery has no
        // AV1 enum / framer, so MapCodec falls AV1 → H.264 at the SDP
        // layer). SIPSorcery still runs its H.264 framer on PT=102
        // packets that actually carry AV1 OBUs, producing garbage frames
        // here. We discard those and instead reassemble in
        // OnRtpPacketReceived using Av1RtpDepacketizer.
        if (_isAv1)
        {
            return;
        }

        // Count bytes at the ARRIVAL point, not post-decode. If we
        // counted in DecodeAndDispatch, a decoder falling behind would
        // make the decode queue drop encoded frames (DropOldest), and
        // those bytes would never be tallied — making the stats overlay
        // read "incoming bitrate decreased" when the network is
        // actually carrying the full rate. Measuring here is the
        // authoritative network-intake rate.
        System.Threading.Interlocked.Increment(ref _framesReceivedCount);
        System.Threading.Interlocked.Add(ref _encodedByteCountAtomic, encodedSample.Length);

        var queue = _decodeQueue;
        if (queue is null)
        {
            return;
        }

        var isKey = IsKeyframe(encodedSample);

        // Drop-to-IDR gate. If we previously drained the decode queue
        // and are waiting for a keyframe, discard non-keyframe input so
        // the decoder never has to decode a P-frame whose reference
        // chain we just broke.
        if (_awaitingKeyframe)
        {
            if (isKey)
            {
                _awaitingKeyframe = false;
                _awaitingKeyframeFramesSeen = 0;
            }
            else
            {
                _awaitingKeyframeFramesSeen++;
                // Intra-refresh streams never emit a classical IDR but
                // self-heal through cyclic intra-coded macroblocks.
                // If we've been waiting past the deadline, give up and
                // resume — the decoder will produce garbage for a
                // refresh period then converge on its own.
                if (_awaitingKeyframeFramesSeen < AwaitingKeyframeMaxFrames)
                {
                    System.Threading.Interlocked.Increment(ref _decodeQueueDroppedTotal);
                    return;
                }
                DebugLog.Write($"[recv {DisplayName}] awaiting-keyframe timed out after {_awaitingKeyframeFramesSeen} non-key frames; resuming without keyframe");
                _awaitingKeyframe = false;
                _awaitingKeyframeFramesSeen = 0;
            }
        }

        // Queue is about to saturate. Instead of letting BoundedChannel's
        // DropOldest policy silently evict a random P-frame (which breaks
        // the reference chain and produces the visible color-smear), drain
        // the queue, arm the await-keyframe gate, and ask the publisher
        // for an IDR via PLI so we don't have to wait for the natural
        // keyframe interval. Clean recovery: brief freeze, then decode
        // resumes against a fresh reference frame.
        if (queue.Reader.Count >= DecodeQueueCapacity - 1)
        {
            DrainDecodeQueueLocked(queue);
            System.Threading.Interlocked.Increment(ref _skipToIdrEventsTotal);
            System.Threading.Interlocked.Increment(ref _decodeQueueDroppedTotal);
            TryRequestKeyframe("decode-queue-drain");
            if (!isKey)
            {
                _awaitingKeyframe = true;
                _awaitingKeyframeFramesSeen = 0;
                return;
            }
            // Lucky: the frame we're holding IS a keyframe, feed it
            // through directly and stay un-armed.
        }

        queue.Writer.TryWrite(new PendingEncodedFrame(encodedSample, encodedSample.Length, timestamp, DateTime.UtcNow, Pooled: false));
    }

    /// <summary>
    /// Classify an encoded frame as a keyframe. H.264: scan the Annex B
    /// bitstream SIPSorcery hands us for a NAL unit of type 5 (IDR).
    /// VP8: bit 0 of the first payload byte is <c>frame_type</c>,
    /// <c>0 = keyframe</c>. Unknown codec → conservatively return false
    /// so the skip-to-IDR logic won't resume on something it can't
    /// classify.
    /// </summary>
    private bool IsKeyframe(byte[] encoded)
    {
        if (encoded is null || encoded.Length == 0)
        {
            return false;
        }
        switch (_decoder.Codec)
        {
            case VideoCodec.H264:
                return ContainsH264Idr(encoded);
            case VideoCodec.Vp8:
                // VP8: bit 0 = frame_type (0=keyframe). Upper bits are
                // the version and show_frame flags — irrelevant here.
                return (encoded[0] & 0x01) == 0;
            default:
                return false;
        }
    }

    private static bool ContainsH264Idr(byte[] buf)
    {
        // Scan for Annex B start codes (00 00 01 or 00 00 00 01) and
        // read the NAL unit header byte immediately after. Low 5 bits
        // are nal_unit_type; type 5 = IDR slice. Typical IDRs ship as
        // SPS (7) + PPS (8) + IDR (5), but the presence of any type-5
        // NAL is sufficient to confirm the frame is decodable without
        // prior reference frames.
        for (var i = 0; i + 3 < buf.Length; i++)
        {
            bool sc3 = buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1;
            bool sc4 = !sc3 && i + 4 < buf.Length
                && buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0 && buf[i + 3] == 1;
            if (!sc3 && !sc4)
            {
                continue;
            }
            var nalOffset = sc3 ? i + 3 : i + 4;
            if (nalOffset >= buf.Length)
            {
                break;
            }
            var nalType = buf[nalOffset] & 0x1F;
            if (nalType == 5)
            {
                return true;
            }
            i = nalOffset;
        }
        return false;
    }

    private void DrainDecodeQueueLocked(System.Threading.Channels.Channel<PendingEncodedFrame> queue)
    {
        while (queue.Reader.TryRead(out var dropped))
        {
            // Discard. Each drained frame was going to be decoded into a
            // P-frame against a reference the decoder was already late on.
            // Return any pool-rented buffer so we don't leak pool entries.
            if (dropped.Pooled && dropped.Bytes is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(dropped.Bytes);
            }
        }
    }

    /// <summary>
    /// Send an RTCP PLI (Picture Loss Indication) to the remote so it
    /// emits a fresh IDR immediately instead of waiting for the next
    /// scheduled keyframe. No-op if we haven't yet observed an RTP
    /// packet to learn the remote's SSRC. Failures are swallowed — the
    /// worst case is we wait for the natural GOP boundary anyway.
    /// </summary>
    private void TryRequestKeyframe() => TryRequestKeyframe(reason: null);

    /// <summary>
    /// External keyframe-recovery trigger from the renderer. Called when
    /// the renderer's stale-pixel probe detects identical center-pixel
    /// output for &gt; threshold time — the decoder claims success and
    /// frames flow through paint, but the actual displayed pixels aren't
    /// changing. This is the "decoder reference state poisoned but no
    /// error surfaced" failure mode that PLI recovery exists for.
    /// </summary>
    public void RequestKeyframeFromRenderer(string reason)
    {
        TryRequestKeyframe($"renderer: {reason}");
    }

    private void TryRequestKeyframe(string? reason)
    {
        var remoteSsrc = _remoteSsrc;
        if (remoteSsrc == 0 || _disposed)
        {
            return;
        }
        // Debounce. If a previous PLI went out within the last
        // PliMinIntervalMs, suppress this one. The SFU produces a single
        // forwarded PLI per coalesce window anyway, and the publisher's
        // CaptureStreamer debounces forced-IDR calls — but capping at
        // the source is cheaper than dragging spurious RTCP packets
        // across the wire.
        lock (_lastPliSentAt)
        {
            if (_lastPliSentAt.IsRunning && _lastPliSentAt.ElapsedMilliseconds < PliMinIntervalMs)
            {
                System.Threading.Interlocked.Increment(ref _pliRequestsSuppressedTotal);
                if (reason is not null)
                {
                    DebugLog.Write($"[recv {DisplayName}] PLI suppressed (debounce, {_lastPliSentAt.ElapsedMilliseconds}ms since last): {reason}");
                }
                return;
            }
            _lastPliSentAt.Restart();
        }
        try
        {
            var localSsrc = _pc.VideoLocalTrack?.Ssrc ?? 0u;
            var feedback = new RTCPFeedback(localSsrc, remoteSsrc, PSFBFeedbackTypesEnum.PLI);
            _pc.SendRtcpFeedback(SDPMediaTypesEnum.video, feedback);
            System.Threading.Interlocked.Increment(ref _pliRequestsSentTotal);
            if (reason is not null)
            {
                DebugLog.Write($"[recv {DisplayName}] PLI sent: {reason}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[recv {DisplayName}] SendRtcpFeedback(PLI) threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs on the dedicated decode worker thread. Synchronous call to the
    /// native decoder + renderer-event invocation. Blocking here is fine —
    /// it only backs up the decode queue, not the UDP receive path.
    /// </summary>
    private long _decodeWorkerSpikeCount;
    private long _decodeQueueDepthSpikeCount;

    private void DecodeAndDispatch(PendingEncodedFrame pending)
    {
        var encodedSample = pending.Bytes;
        var sampleLength = pending.Length;
        var arrival = pending.ArrivalUtc;
        // Layer 3a: decode-worker queue depth probe. Logs whenever
        // the input queue (the channel feeding the decode worker)
        // exceeds 5 — meaning the worker is falling behind the
        // network. Catches the precursor pattern that leads to
        // 0xC00D36CB cascades (decoder running on the edge).
        var qd = _decodeQueue?.Reader.Count ?? 0;
        if (qd > 5)
        {
            var n = ++_decodeQueueDepthSpikeCount;
            if (n <= 200 || n % 50 == 0)
            {
                DebugLog.Write($"[recv-l3-decq {DisplayName}] decode-worker queue={qd}/{DecodeQueueCapacity} ts={pending.RtpTimestamp}");
            }
        }
        var workerT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            DecodeAndDispatchInner(pending, encodedSample, sampleLength, arrival);
        }
        finally
        {
            if (pending.Pooled && encodedSample is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(encodedSample);
            }
            // Layer 3b: total decode-worker iter time. Includes
            // _decoder.Decode + every renderer-event subscriber +
            // any byte[] return-to-pool. At 144 fps the budget is
            // ~7 ms; >12 ms means the worker can't sustain cadence.
            var workerMs = (System.Diagnostics.Stopwatch.GetTimestamp() - workerT0) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
            if (workerMs > 12.0)
            {
                var n = ++_decodeWorkerSpikeCount;
                if (n <= 200 || n % 50 == 0)
                {
                    DebugLog.Write($"[recv-l3-worker {DisplayName}] iter={workerMs:F1}ms bytes={sampleLength} ts={pending.RtpTimestamp}");
                }
            }
        }
    }

    private void DecodeAndDispatchInner(PendingEncodedFrame pending, byte[] encodedSample, int sampleLength, DateTime arrival)
    {

        System.Collections.Generic.IReadOnlyList<DecodedVideoFrame> frames;
        var gap = _lastFrameUtc == DateTime.MinValue ? TimeSpan.Zero : arrival - _lastFrameUtc;
        _lastFrameUtc = arrival;
        if (gap > TimeSpan.FromSeconds(2))
        {
            DebugLog.Write($"[recv] incoming packet after {gap.TotalSeconds:F1}s gap — stream restarted");
        }

        // Convert the RTP 90 kHz clock to a TimeSpan and hand it to the
        // decoder. The decoder propagates this through MF SampleTime so
        // each DecodedVideoFrame.Timestamp is authoritative — not
        // something we reconstruct per frame here. When the decoder yields
        // multiple outputs in one call (buffered older frame plus a new
        // one) each output carries its OWN original timestamp.
        var rtpInputTs = TimeSpan.FromTicks((long)pending.RtpTimestamp * TimeSpan.TicksPerMillisecond / 90);

        int gpuEmittedThisCall;
        lock (_decodeLock)
        {
            if (_disposed)
            {
                return;
            }

            // FramesReceived + EncodedByteCount now tick at network
            // arrival in OnVideoFrameReceived — not here — so the stats
            // overlay reads real intake rate regardless of what the
            // decode queue discards under backpressure.
            _gpuEmittedThisCall = 0;
            System.Threading.Interlocked.Increment(ref _decodeWorkerRanTotal);
            try
            {
                frames = _decoder.Decode(encodedSample, sampleLength, rtpInputTs);
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref _decoderExceptionsTotal);
                // Decoder error mid-stream (packet loss, malformed SPS, etc.)
                // should not tear down the receive path. Log, request a
                // fresh keyframe upstream, skip this frame.
                DebugLog.Write($"[recv] decoder threw: {ex.Message}");
                TryRequestKeyframe($"decoder threw {ex.GetType().Name}");
                return;
            }
            gpuEmittedThisCall = _gpuEmittedThisCall;
        }

        // GPU fast path: decoder invoked our GpuOutputHandler synchronously
        // inside Decode, so TextureArrived has already fired and
        // FramesDecoded / LastWidth / LastHeight were updated there.
        // `frames` is empty by construction in that case.
        if (gpuEmittedThisCall > 0)
        {
            return;
        }

        if (frames.Count == 0)
        {
            return;
        }

        foreach (var decoded in frames)
        {
            if (decoded.Bgra is null || decoded.Bgra.Length == 0)
            {
                continue;
            }

            var bgraSize = decoded.Width * decoded.Height * 4;
            if (decoded.Bgra.Length < bgraSize)
            {
                continue;
            }

            FramesDecoded++;
            LastWidth = decoded.Width;
            LastHeight = decoded.Height;
            // Watchdog stamp on the CPU output path — counterpart of the
            // GPU stamp in OnDecoderGpuFrame.
            _lastFrameOutputTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            var frame = new CaptureFrameData(
                decoded.Bgra.AsSpan(0, bgraSize),
                decoded.Width,
                decoded.Height,
                strideBytes: decoded.Width * 4,
                format: CaptureFramePixelFormat.Bgra8,
                timestamp: decoded.Timestamp);

            // Invoke each subscriber individually — see OnDecoderGpuFrame
            // for the reasoning. Default multicast Invoke would let one
            // thrown handler (e.g. a stray UI-thread-affine read) suppress
            // the renderer's paint and silently leave the tile at
            // "Connecting".
            var handlers = FrameArrived?.GetInvocationList();
            if (handlers is null)
            {
                continue;
            }
            foreach (FrameArrivedHandler handler in handlers)
            {
                try { handler(in frame); }
                catch (Exception ex)
                {
                    DebugLog.Write($"[recv {DisplayName}] FrameArrived handler {handler.Method.DeclaringType?.Name}.{handler.Method.Name} threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    // Fires synchronously from inside _decoder.Decode when the decoder
    // opts into zero-copy GPU output. Bumps the per-frame counters the
    // CPU path normally maintains so the stats overlay still reads
    // correctly, then raises TextureArrived for subscribers (the renderer)
    // to GPU-copy the texture during this call. _gpuEmittedThisCall lets
    // OnVideoFrameReceived tell a genuine empty decode apart from a
    // GPU-path decode that legitimately returned no CPU bytes.
    private int _gpuEmittedThisCall;

    private long _gpuFrameCount;

    private void OnDecoderGpuFrame(IntPtr texture, int width, int height, TimeSpan timestamp)
    {
        FramesDecoded++;
        LastWidth = width;
        LastHeight = height;
        _gpuEmittedThisCall++;
        // Watchdog stamp: a real decoded frame just came out, so the
        // pipeline isn't wedged. The PLI watchdog in EmitPulse uses
        // this to decide whether to fire a recovery request.
        _lastFrameOutputTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = System.Threading.Interlocked.Increment(ref _gpuFrameCount);
        if (n == 1)
        {
            var subs = TextureArrived?.GetInvocationList().Length ?? 0;
            DebugLog.Write($"[recv {DisplayName} hash={GetHashCode():X8}] GpuFrame#1 {width}x{height} TextureArrivedSubs={subs}");
        }

        // Invoke each subscriber individually so one throwing handler doesn't
        // short-circuit the rest. C# multicast event Invoke stops iterating
        // on the first thrown exception — that meant a UI-thread-affine
        // touch in an early subscriber (e.g. a tile VM accidentally reading
        // a DependencyProperty) silently prevented the renderer's
        // OnTextureArrived from ever running, so the tile stayed at
        // "Connecting" with the receiver decoding fine.
        var handlers = TextureArrived?.GetInvocationList();
        if (handlers is null || handlers.Length == 0)
        {
            return;
        }

        // Self-balanced refcount contract: the decoder does NOT
        // pre-AddRef before fanning out. Each subscriber that needs
        // the texture must AddRef itself before wrapping; observers
        // that don't wrap don't have to do anything. This receiver
        // doesn't manipulate refcount — it just multicasts.
        foreach (TextureArrivedHandler handler in handlers)
        {
            try { handler(texture, width, height, timestamp); }
            catch (Exception ex)
            {
                DebugLog.Write($"[recv {DisplayName}] TextureArrived handler {handler.Method.DeclaringType?.Name}.{handler.Method.Name} threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

}
