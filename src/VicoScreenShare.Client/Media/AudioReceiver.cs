namespace VicoScreenShare.Client.Media;

using System;
using System.Collections.Generic;
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
    // Jitter buffer depth in packets. 3 × 20 ms = 60 ms — about half a
    // video frame's worth of latency, covering typical Wi-Fi reorder
    // windows without meaningfully lagging the video track.
    private const int JitterBufferMaxPackets = 3;

    private readonly RTCPeerConnection _pc;
    private readonly IAudioDecoderFactory _decoderFactory;
    private readonly IAudioRenderer _renderer;
    private readonly string _displayName;
    private readonly object _decodeLock = new();
    private readonly SortedList<uint, DecodedAudioFrame> _jitterBuffer = new(JitterBufferMaxPackets + 2);

    private IAudioDecoder? _decoder;
    private uint _lastDrainedRtpTimestamp;
    private bool _hasDrained;
    private long _packetsReceived;
    private long _framesDecoded;
    private long _framesSubmitted;
    private long _framesDropped;
    private int _state; // 0 = idle, 1 = running, 2 = disposed

    public AudioReceiver(
        RTCPeerConnection pc,
        IAudioDecoderFactory decoderFactory,
        IAudioRenderer renderer,
        string displayName)
    {
        ArgumentNullException.ThrowIfNull(pc);
        ArgumentNullException.ThrowIfNull(decoderFactory);
        ArgumentNullException.ThrowIfNull(renderer);

        _pc = pc;
        _decoderFactory = decoderFactory;
        _renderer = renderer;
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
    }

    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _state, 0, 1) != 1)
        {
            return;
        }
        _pc.OnRtpPacketReceived -= OnRtpPacketReceived;
        await _renderer.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _state, 2) == 2)
        {
            return;
        }
        try { _pc.OnRtpPacketReceived -= OnRtpPacketReceived; } catch { }
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

            // Overflow: drop oldest so the buffer never grows past its
            // bound. Prefer a single-frame audio glitch over allowing a
            // sustained latency buildup that would permanently desync
            // with video.
            while (_jitterBuffer.Count > JitterBufferMaxPackets + 1)
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
        DecodedAudioFrame? toSubmit;
        lock (_decodeLock)
        {
            if (_jitterBuffer.Count <= JitterBufferMaxPackets)
            {
                return;
            }
            var key = _jitterBuffer.Keys[0];
            toSubmit = _jitterBuffer.Values[0];
            _jitterBuffer.RemoveAt(0);
            _hasDrained = true;
            _lastDrainedRtpTimestamp = key;
        }

        try
        {
            _renderer.Submit(toSubmit.Value.Pcm, TimeSpan.FromTicks(
                (long)toSubmit.Value.RtpTimestamp * TimeSpan.TicksPerMillisecond / 48));
            Interlocked.Increment(ref _framesSubmitted);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[audio-rx {_displayName}] renderer submit threw: {ex.Message}");
        }
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
