namespace VicoScreenShare.Client.Media;

using System;
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

    private readonly record struct PendingEncodedFrame(byte[] Bytes, uint RtpTimestamp, DateTime ArrivalUtc);

    // RFC 3550 §A.3 sequence-stats for this receiver's incoming video RTP.
    // Derived loss, reorder-aware — see RtpSequenceStats for the math.
    private readonly RtpSequenceStats _rtpStats = new();
    private uint _lastLoggedSsrc;

    public StreamReceiver(RTCPeerConnection pc, string displayName = "remote")
        : this(pc, new VpxDecoderFactory(), displayName)
    {
    }

    public StreamReceiver(RTCPeerConnection pc, IVideoDecoderFactory decoderFactory, string displayName = "remote")
    {
        _pc = pc;
        DisplayName = displayName;
        _decoder = decoderFactory.CreateDecoder();
        // Opt the decoder into GPU-resident output. Decoders that don't
        // support it (VPX, and MF decoders with no shared D3D device) use
        // the default no-op setter; their frames still flow via the CPU
        // byte[] path. MF + shared device emits here, which saves the
        // per-frame BGRA readback + upload round-trip.
        _decoder.GpuOutputHandler = OnDecoderGpuFrame;
    }

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

    public event Action? Closed;

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
        return System.Threading.Tasks.Task.CompletedTask;
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
        DebugLog.Write(
            $"[recv-pulse {DisplayName}] 2s: rcvd=+{dR} dec=+{dWr} dropped=+{dDrop} excep=+{dExc} " +
            $"skip2idr=+{dSkip} decQ={qCount}/{DecodeQueueCapacity} bw={mbps:F1} Mbps");
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
    /// used by <see cref="RtpLossPercent"/>. Runs on SIPSorcery's receive
    /// thread; keep this path branch-light and allocation-free.
    /// </summary>
    private void OnRtpPacketReceived(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
    {
        if (mediaType != SDPMediaTypesEnum.video || packet is null)
        {
            return;
        }

        var ssrc = packet.Header.SyncSource;
        var seq = packet.Header.SequenceNumber;

        // One-shot log on SSRC transition. Lock-free because Observe itself
        // is the authoritative tracker of SSRC changes; this comparison only
        // drives a diagnostic line and tolerates races.
        if (_lastLoggedSsrc != 0 && _lastLoggedSsrc != ssrc)
        {
            DebugLog.Write($"[recv {DisplayName}] SSRC changed 0x{_lastLoggedSsrc:X8} -> 0x{ssrc:X8} at seq {seq}");
        }
        _lastLoggedSsrc = ssrc;
        _remoteSsrc = ssrc;

        _rtpStats.Observe(ssrc, seq);
    }

    public System.Threading.Tasks.ValueTask DisposeAsync()
    {
        Dispose();
        return System.Threading.Tasks.ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        // Stop new frames first, then take the decode lock so any in-flight
        // Decode call finishes before we free the native decoder state.
        // Without this pairing we crash the CLR with ExecutionEngineException
        // when a frame arrives concurrently with Dispose.
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        lock (_decodeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { _decoder.Dispose(); } catch { }
        }
    }

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        if (state is RTCPeerConnectionState.closed
                  or RTCPeerConnectionState.failed
                  or RTCPeerConnectionState.disconnected)
        {
            Closed?.Invoke();
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
            TryRequestKeyframe();
            if (!isKey)
            {
                _awaitingKeyframe = true;
                _awaitingKeyframeFramesSeen = 0;
                return;
            }
            // Lucky: the frame we're holding IS a keyframe, feed it
            // through directly and stay un-armed.
        }

        queue.Writer.TryWrite(new PendingEncodedFrame(encodedSample, timestamp, DateTime.UtcNow));
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
        while (queue.Reader.TryRead(out _))
        {
            // Discard. Each drained frame was going to be decoded into a
            // P-frame against a reference the decoder was already late on.
        }
    }

    /// <summary>
    /// Send an RTCP PLI (Picture Loss Indication) to the remote so it
    /// emits a fresh IDR immediately instead of waiting for the next
    /// scheduled keyframe. No-op if we haven't yet observed an RTP
    /// packet to learn the remote's SSRC. Failures are swallowed — the
    /// worst case is we wait for the natural GOP boundary anyway.
    /// </summary>
    private void TryRequestKeyframe()
    {
        var remoteSsrc = _remoteSsrc;
        if (remoteSsrc == 0 || _disposed)
        {
            return;
        }
        try
        {
            var localSsrc = _pc.VideoLocalTrack?.Ssrc ?? 0u;
            var feedback = new RTCPFeedback(localSsrc, remoteSsrc, PSFBFeedbackTypesEnum.PLI);
            _pc.SendRtcpFeedback(SDPMediaTypesEnum.video, feedback);
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
    private void DecodeAndDispatch(PendingEncodedFrame pending)
    {
        var encodedSample = pending.Bytes;
        var arrival = pending.ArrivalUtc;

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
                frames = _decoder.Decode(encodedSample, rtpInputTs);
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref _decoderExceptionsTotal);
                // Decoder error mid-stream (packet loss, malformed SPS, etc.)
                // should not tear down the receive path. Log and skip.
                DebugLog.Write($"[recv] decoder threw: {ex.Message}");
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

            var frame = new CaptureFrameData(
                decoded.Bgra.AsSpan(0, bgraSize),
                decoded.Width,
                decoded.Height,
                strideBytes: decoded.Width * 4,
                format: CaptureFramePixelFormat.Bgra8,
                timestamp: decoded.Timestamp);
            FrameArrived?.Invoke(in frame);
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

    private void OnDecoderGpuFrame(IntPtr texture, int width, int height, TimeSpan timestamp)
    {
        FramesDecoded++;
        LastWidth = width;
        LastHeight = height;
        _gpuEmittedThisCall++;
        try { TextureArrived?.Invoke(texture, width, height, timestamp); }
        catch (Exception ex)
        {
            DebugLog.Write($"[recv] TextureArrived handler threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
