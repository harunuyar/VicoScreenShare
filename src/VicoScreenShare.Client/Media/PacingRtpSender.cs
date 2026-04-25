namespace VicoScreenShare.Client.Media;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SIPSorcery.Net;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Send-side packet pacer. Sits between the video encoder callback and
/// SIPSorcery's RTP send path, releasing individual RTP packets onto the
/// wire at a configurable target bitrate instead of dumping a whole encoded
/// frame's packets back-to-back.
///
/// Why this exists: NVENC produces ~1 MB H.264 keyframes that hit the UDP
/// socket in &lt; 2 ms at gigabit, then traverse the SFU to a viewer at the
/// same line rate. A viewer on a 30 Mbit downlink can't drain that burst
/// fast enough — their modem queue overruns and the tail of the keyframe
/// drops, causing visible blocking on every IDR. Pacing the same keyframe
/// at the encoder's target bitrate (e.g. 12 Mbps) spreads it over hundreds
/// of milliseconds, so it arrives at the viewer's downlink at a rate the
/// link can absorb.
///
/// Tradeoff: one-time per-keyframe latency = (keyframe size in bits) /
/// (pace rate in bps). At 12 Mbps a 1 MB keyframe takes ~660 ms on the
/// wire instead of &lt; 2 ms. The receiver's content-PTS pacer
/// (PaintLoop + TimestampedFrameQueue) anchors on the first frame's wall
/// time and steady-state cadence is unchanged after that — the latency is
/// a one-time cost, not a per-frame one.
///
/// Threading model:
/// <list type="bullet">
/// <item><see cref="Enqueue"/> may be called from any thread (the encoder
/// callback). It only takes the queue lock briefly.</item>
/// <item>A dedicated <see cref="ThreadPriority.AboveNormal"/> background
/// thread (<c>RtpPacer</c>) drains the queue, packetises each frame using
/// <see cref="H264Packetiser"/> for H.264 / inline VP8 logic, and calls
/// the configured <see cref="SendRtpRawDelegate"/> per packet with a
/// computed inter-packet sleep.</item>
/// </list>
///
/// Drop policy: when <see cref="CurrentQueueDepth"/> would exceed
/// <c>maxQueueDepthFrames</c>, the oldest non-keyframe in the queue is
/// evicted to make room. A keyframe is never dropped — the receiver
/// decoder can't recover without one and would freeze until the next IDR
/// (potentially seconds, depending on GOP).
///
/// Tested headlessly via the <c>bench-pacer</c> MediaHarness scenario,
/// which feeds synthetic frames through the pacer with a stub
/// <see cref="SendRtpRawDelegate"/> and asserts the inter-packet
/// timing matches the configured rate.
/// </summary>
public sealed class PacingRtpSender : IDisposable
{
    /// <summary>
    /// One pre-formed RTP packet payload destined for
    /// <c>RTCPeerConnection.SendRtpRaw</c>. The pacer uses this signature
    /// so production wires it to SIPSorcery and tests wire it to a
    /// recording stub.
    /// </summary>
    public delegate void SendRtpRawDelegate(byte[] payload, uint rtpTimestamp, int markerBit, int payloadTypeId);

    /// <summary>
    /// Mirrors <c>RTPSession.RTP_MAX_PAYLOAD</c> (which is
    /// <c>protected internal</c> and so unreachable from outside the
    /// SIPSorcery assembly). Both must agree — if SIPSorcery changes its
    /// constant we must update this one to match.
    /// </summary>
    public const int RtpMaxPayload = 1200;

    private readonly SendRtpRawDelegate _send;
    private readonly Func<int> _getPayloadTypeId;
    // Codec the *encoder* produced — used only for keyframe detection
    // (Annex-B IDR scan vs VP8 frame-tag bit). The packetiser uses
    // _framingCodec, which can differ when our app's SDP intersection
    // negotiated a different RTP payload type than the encoder's
    // actual format.
    private readonly VideoCodec _contentCodec;
    // Codec dictated by the negotiated payload type. Determines which
    // RTP packetiser we use so the receiver's matching framer
    // reassembles cleanly. The existing non-pacer path (SendVideo) uses
    // this same codec for packetisation regardless of byte content —
    // because the framing is the wire-level contract, not the codec
    // identity. Mismatching here breaks reassembly even when both
    // sides agree on the inner codec.
    private readonly VideoCodec _framingCodec;
    private readonly int _maxQueueDepthFrames;
    private readonly LinkedList<QueuedFrame> _queue = new();
    private readonly object _queueLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _paceThread;
    private readonly CancellationTokenSource _cts = new();

    private uint _rtpTimestamp;
    private long _nextSendTicks;
    private long _packetsSent;
    private long _framesSent;
    private long _framesQueued;
    private long _framesDropped;
    private long _missingPayloadTypeDrops;
    private int _targetBitsPerSecond;
    private bool _disposed;
    private bool _firstSendLogged;
    private System.Threading.Timer? _pulseTimer;
    private long _pulseLastQueued;
    private long _pulseLastSent;
    private long _pulseLastPackets;
    private long _pulseLastDropped;

    public PacingRtpSender(
        SendRtpRawDelegate sendRtpRaw,
        Func<int> getPayloadTypeId,
        uint initialRtpTimestamp,
        VideoCodec contentCodec,
        VideoCodec framingCodec,
        int targetBitsPerSecond,
        int maxQueueDepthFrames = 30)
    {
        _send = sendRtpRaw ?? throw new ArgumentNullException(nameof(sendRtpRaw));
        _getPayloadTypeId = getPayloadTypeId ?? throw new ArgumentNullException(nameof(getPayloadTypeId));
        _contentCodec = contentCodec;
        _framingCodec = framingCodec;
        _maxQueueDepthFrames = Math.Max(2, maxQueueDepthFrames);
        _targetBitsPerSecond = Math.Max(100_000, targetBitsPerSecond);
        _rtpTimestamp = initialRtpTimestamp;
        _nextSendTicks = Stopwatch.GetTimestamp();

        _paceThread = new Thread(PaceLoop)
        {
            IsBackground = true,
            Name = "RtpPacer",
            Priority = ThreadPriority.AboveNormal,
        };
        _paceThread.Start();
        _pulseTimer = new System.Threading.Timer(_ => EmitPulse(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        DebugLog.Write($"[pacer] started content={contentCodec} framing={framingCodec} rate={_targetBitsPerSecond / 1000} kbps maxQueue={_maxQueueDepthFrames} frames");
    }

    /// <summary>
    /// Updateable pacing rate in bits per second. The adaptive-bitrate
    /// controller calls this in lock-step with the encoder's
    /// <c>UpdateBitrate</c> so the wire pace matches the new mean rate.
    /// </summary>
    public int TargetBitsPerSecond
    {
        get => Volatile.Read(ref _targetBitsPerSecond);
        set => Volatile.Write(ref _targetBitsPerSecond, Math.Max(100_000, value));
    }

    public long FramesQueued => Interlocked.Read(ref _framesQueued);
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);
    public long PacketsSent => Interlocked.Read(ref _packetsSent);
    public int CurrentQueueDepth { get { lock (_queueLock) { return _queue.Count; } } }

    /// <summary>
    /// Hand an encoded frame to the pacer. Returns immediately — actual
    /// send happens on the pacer's own thread. The <paramref name="duration"/>
    /// is in 90 kHz RTP clock units (matching SIPSorcery's
    /// <c>SendVideo(uint duration, ...)</c> contract).
    /// </summary>
    public void Enqueue(uint duration, byte[] sample)
    {
        if (_disposed || sample is null || sample.Length == 0)
        {
            return;
        }

        // Keyframe detection examines the actual encoded bytes, so it
        // must use the encoder's codec — NOT the framing codec, which
        // may disagree if SDP intersection picked a different PT.
        var isKey = DetectKeyframe(sample, _contentCodec);
        var frame = new QueuedFrame
        {
            Duration = duration,
            Sample = sample,
            IsKeyframe = isKey,
            EnqueuedTicks = Stopwatch.GetTimestamp(),
        };

        var dropped = false;
        lock (_queueLock)
        {
            if (_queue.Count >= _maxQueueDepthFrames)
            {
                // Evict the oldest non-keyframe to make room. Walk from
                // the front (oldest) and remove the first P-frame found.
                // If the queue is somehow all keyframes (shouldn't happen
                // in normal operation), accept the new frame anyway —
                // dropping a keyframe to make room for another keyframe
                // achieves nothing.
                for (var node = _queue.First; node is not null; node = node.Next)
                {
                    if (!node.Value.IsKeyframe)
                    {
                        _queue.Remove(node);
                        dropped = true;
                        break;
                    }
                }
            }
            _queue.AddLast(frame);
        }

        Interlocked.Increment(ref _framesQueued);
        if (dropped)
        {
            Interlocked.Increment(ref _framesDropped);
        }
        _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _pulseTimer?.Dispose(); } catch { }
        try { _cts.Cancel(); } catch { }
        _wake.Set();
        try { _paceThread.Join(500); } catch { }
        try { _wake.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
        DebugLog.Write($"[pacer] disposed: queued={FramesQueued} sent={FramesSent} dropped={FramesDropped} packets={PacketsSent} payloadTypeDrops={Interlocked.Read(ref _missingPayloadTypeDrops)}");
    }

    /// <summary>
    /// 2-second pulse logger so the publisher's debug.log shows what the
    /// pacer is doing at runtime. Mirrors the existing paint-pulse format
    /// so a screen-share session log reads consistently across components.
    /// </summary>
    private void EmitPulse()
    {
        if (_disposed)
        {
            return;
        }
        var queued = Interlocked.Read(ref _framesQueued);
        var sent = Interlocked.Read(ref _framesSent);
        var packets = Interlocked.Read(ref _packetsSent);
        var dropped = Interlocked.Read(ref _framesDropped);
        var dQueued = queued - _pulseLastQueued; _pulseLastQueued = queued;
        var dSent = sent - _pulseLastSent; _pulseLastSent = sent;
        var dPackets = packets - _pulseLastPackets; _pulseLastPackets = packets;
        var dDropped = dropped - _pulseLastDropped; _pulseLastDropped = dropped;
        if (dQueued == 0 && dSent == 0 && dPackets == 0 && dDropped == 0)
        {
            return;
        }
        DebugLog.Write(
            $"[pacer-pulse] 2s: queued=+{dQueued} sent=+{dSent} pkts=+{dPackets} drops=+{dDropped} "
            + $"qDepth={CurrentQueueDepth} rate={TargetBitsPerSecond / 1000} kbps");
    }

    private bool TryDequeue(out QueuedFrame frame)
    {
        lock (_queueLock)
        {
            if (_queue.Count == 0)
            {
                frame = default;
                return false;
            }
            frame = _queue.First!.Value;
            _queue.RemoveFirst();
            return true;
        }
    }

    private void PaceLoop()
    {
        var wasIdle = true;
        while (!_cts.IsCancellationRequested)
        {
            if (!TryDequeue(out var frame))
            {
                // Idle wait. The 100 ms timeout caps the worst-case
                // latency for a frame that arrives while we're sleeping
                // past our last-sent deadline.
                try { _wake.WaitOne(100); }
                catch (ObjectDisposedException) { return; }
                wasIdle = true;
                continue;
            }

            // Reset the wire-time anchor when we transition from idle to
            // busy. Without this, _nextSendTicks would still hold a value
            // from before the idle gap and the pacer would burst the
            // first ~Wgap/rate worth of bytes without pacing. Resetting
            // here, and *only* here, means continuous-flow operation
            // paces strictly to the configured rate while a fresh start
            // (or a stretch of static screen) doesn't penalise the next
            // burst by enforcing a fictitious "catch-up" deadline.
            if (wasIdle)
            {
                _nextSendTicks = Stopwatch.GetTimestamp();
                wasIdle = false;
            }

            try
            {
                SendFrame(frame);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[pacer] SendFrame threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void SendFrame(QueuedFrame frame)
    {
        var payloadType = _getPayloadTypeId();
        if (payloadType < 0)
        {
            // SDP not yet negotiated. Tally so the pulse / disposal log
            // surfaces this as a real failure mode if it happens
            // repeatedly — the encoder shouldn't be producing frames
            // before the PC is connected, and a sustained non-zero
            // counter here means the wiring raced.
            Interlocked.Increment(ref _missingPayloadTypeDrops);
            return;
        }

        var ts = _rtpTimestamp;

        // Packetisation uses the framing codec (driven by the negotiated
        // RTP payload type), which can differ from the encoder's actual
        // output. The receiver's RtpVideoFramer is selected by the
        // payload type's codec; it strips a VP8 1-byte header from each
        // packet when PT maps to VP8, and processes FU-A / single-NAL
        // packets when PT maps to H.264. Whatever bytes we put inside
        // the framing reassemble cleanly on the other side as long as
        // both sides agree on the framing — the encoder's codec is
        // orthogonal.
        var packets = _framingCodec switch
        {
            VideoCodec.H264 => PacketiseH264(frame.Sample),
            VideoCodec.Vp8 => PacketiseVp8(frame.Sample),
            _ => PacketiseGeneric(frame.Sample),
        };

        for (var i = 0; i < packets.Count; i++)
        {
            var packet = packets[i];
            var isLast = i == packets.Count - 1;
            var markerBit = isLast ? 1 : 0;

            // Wait until our scheduled slot. The slot is computed on the
            // packet's wire-time cost so the rolling rate matches
            // TargetBitsPerSecond regardless of frame composition.
            WaitForNextSlot();
            try
            {
                _send(packet, ts, markerBit, payloadType);
                if (!_firstSendLogged)
                {
                    _firstSendLogged = true;
                    DebugLog.Write($"[pacer] first packet sent: payloadType={payloadType} ts={ts} marker={markerBit} size={packet.Length}");
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[pacer] send threw: {ex.GetType().Name}: {ex.Message}");
            }
            Interlocked.Increment(ref _packetsSent);

            // Advance the wire-time anchor by exactly this packet's
            // serialisation cost. Drift is intentional: if the pacer
            // can't keep up (encoder out-producing the wire rate), the
            // queue grows and the drop policy kicks in — this is correct
            // backpressure and what we want. The wasIdle reset in
            // PaceLoop is the *only* place _nextSendTicks gets snapped
            // back to wall time.
            var packetBits = packet.Length * 8L;
            var rate = TargetBitsPerSecond;
            var ticksPerPacket = packetBits * Stopwatch.Frequency / rate;
            _nextSendTicks += ticksPerPacket;
        }

        _rtpTimestamp += frame.Duration;
        Interlocked.Increment(ref _framesSent);
    }

    private void WaitForNextSlot()
    {
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var remainingTicks = _nextSendTicks - now;
            if (remainingTicks <= 0)
            {
                return;
            }
            var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2.0)
            {
                Thread.Sleep((int)(remainingMs - 1.0));
            }
            else
            {
                // Sub-millisecond tail — spin to hit the deadline more
                // precisely than Thread.Sleep can guarantee.
                Thread.SpinWait(50);
            }
        }
    }

    /// <summary>
    /// H.264 keyframe = the access unit contains a NAL of type 5 (IDR
    /// slice). Scanning Annex-B start codes is enough — we don't need a
    /// full parse, only a yes/no for the drop policy.
    /// </summary>
    private static bool DetectKeyframe(byte[] sample, VideoCodec codec)
    {
        if (codec == VideoCodec.H264)
        {
            for (var i = 0; i + 3 < sample.Length; i++)
            {
                if (sample[i] != 0 || sample[i + 1] != 0)
                {
                    continue;
                }

                int nalStart;
                if (sample[i + 2] == 1)
                {
                    nalStart = i + 3;
                }
                else if (sample[i + 2] == 0 && sample[i + 3] == 1)
                {
                    nalStart = i + 4;
                }
                else
                {
                    continue;
                }
                if (nalStart < sample.Length && (sample[nalStart] & 0x1F) == 5)
                {
                    return true;
                }
            }
            return false;
        }

        if (codec == VideoCodec.Vp8)
        {
            // VP8 frame tag bit 0 of byte 0: 0 = keyframe, 1 = interframe.
            return sample.Length > 0 && (sample[0] & 0x01) == 0;
        }

        // Unknown / AV1 — treat as non-keyframe; the drop policy still
        // works (just less safely on unknown codecs).
        return false;
    }

    /// <summary>
    /// Mirror of <c>VideoStream.SendH26XNal</c> packetisation, returning
    /// the per-packet payload byte arrays instead of writing them to the
    /// socket. Single-NAL packets &lt;= <see cref="RtpMaxPayload"/> are
    /// emitted as STAP-A (one packet, full NAL); larger ones are
    /// fragmented as FU-A using <see cref="H264Packetiser.GetH264RtpHeader"/>.
    /// </summary>
    private static List<byte[]> PacketiseH264(byte[] accessUnit)
    {
        var packets = new List<byte[]>(8);
        foreach (var nal in H264Packetiser.ParseNals(accessUnit))
        {
            var nalBytes = nal.NAL;
            if (nalBytes.Length <= RtpMaxPayload)
            {
                // Single-Time Aggregation Packet, type A.
                var single = new byte[nalBytes.Length];
                Buffer.BlockCopy(nalBytes, 0, single, 0, nalBytes.Length);
                packets.Add(single);
                continue;
            }

            // Fragmentation Unit A. The first byte is the NAL header
            // (preserved in GetH264RtpHeader); the remaining NAL payload
            // is split into RtpMaxPayload-sized fragments.
            var nalHeader = nalBytes[0];
            var payloadOnly = new byte[nalBytes.Length - 1];
            Buffer.BlockCopy(nalBytes, 1, payloadOnly, 0, payloadOnly.Length);

            for (var index = 0; index * RtpMaxPayload < payloadOnly.Length; index++)
            {
                var offset = index * RtpMaxPayload;
                var payloadLength = ((index + 1) * RtpMaxPayload < payloadOnly.Length)
                    ? RtpMaxPayload
                    : payloadOnly.Length - offset;

                var isFirstPacket = index == 0;
                var isFinalPacket = (index + 1) * RtpMaxPayload >= payloadOnly.Length;

                var fuHeader = H264Packetiser.GetH264RtpHeader(nalHeader, isFirstPacket, isFinalPacket);
                var packet = new byte[fuHeader.Length + payloadLength];
                Buffer.BlockCopy(fuHeader, 0, packet, 0, fuHeader.Length);
                Buffer.BlockCopy(payloadOnly, offset, packet, fuHeader.Length, payloadLength);
                packets.Add(packet);
            }
        }
        return packets;
    }

    /// <summary>
    /// Mirror of <c>VideoStream.SendVp8Frame</c> inline packetisation
    /// (lines 245-260 in upstream sipsorcery). Splits the encoded VP8
    /// frame into <see cref="RtpMaxPayload"/>-sized chunks and prepends
    /// the 1-byte VP8 RTP header (<c>0x10</c> on the first chunk to set
    /// the start-of-partition bit, <c>0x00</c> on continuation chunks).
    /// </summary>
    private static List<byte[]> PacketiseVp8(byte[] frame)
    {
        var packets = new List<byte[]>(4);
        for (var index = 0; index * RtpMaxPayload < frame.Length; index++)
        {
            var offset = index * RtpMaxPayload;
            var payloadLength = (offset + RtpMaxPayload < frame.Length)
                ? RtpMaxPayload
                : frame.Length - offset;

            var vp8Header = (index == 0) ? (byte)0x10 : (byte)0x00;
            var packet = new byte[1 + payloadLength];
            packet[0] = vp8Header;
            Buffer.BlockCopy(frame, offset, packet, 1, payloadLength);
            packets.Add(packet);
        }
        return packets;
    }

    /// <summary>
    /// Last-resort packetisation for codecs we don't have format-specific
    /// logic for. Splits at MTU boundaries with no codec header. Used so
    /// the pacer never crashes on an unexpected codec — production picks
    /// up VP8 / H.264 paths above.
    /// </summary>
    private static List<byte[]> PacketiseGeneric(byte[] frame)
    {
        var packets = new List<byte[]>(4);
        for (var index = 0; index * RtpMaxPayload < frame.Length; index++)
        {
            var offset = index * RtpMaxPayload;
            var payloadLength = (offset + RtpMaxPayload < frame.Length)
                ? RtpMaxPayload
                : frame.Length - offset;
            var packet = new byte[payloadLength];
            Buffer.BlockCopy(frame, offset, packet, 0, payloadLength);
            packets.Add(packet);
        }
        return packets;
    }

    private struct QueuedFrame
    {
        public uint Duration;
        public byte[] Sample;
        public bool IsKeyframe;
        public long EnqueuedTicks;
    }
}
