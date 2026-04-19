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
        return System.Threading.Tasks.Task.CompletedTask;
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

        // Writer is single-writer (this thread). TryWrite is non-blocking
        // even on a full channel — FullMode=DropOldest means an old queued
        // frame gets evicted, freeing room for the new one. This keeps the
        // receive thread bounded to a few microseconds per callback
        // regardless of renderer backpressure.
        queue.Writer.TryWrite(new PendingEncodedFrame(encodedSample, timestamp, DateTime.UtcNow));
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
            try
            {
                frames = _decoder.Decode(encodedSample, rtpInputTs);
            }
            catch (Exception ex)
            {
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
