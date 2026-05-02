namespace VicoScreenShare.Server.Sfu;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

/// <summary>
/// Server-side one-way "subscriber" peer connection: sends a single publisher's
/// video out to a single viewer. One of these is created per (viewer, publisher)
/// pair on <see cref="SfuSession.OnPublisherStarted"/> and torn down on
/// <see cref="SfuSession.OnPublisherStopped"/> or when either end leaves.
///
/// The server is the offerer for this PC (unlike <see cref="SfuPeer"/>, where
/// the client offers). That lets us add the publisher's send track on the
/// server before any SDP exists, then drive the handshake via a server-initiated
/// <c>SdpOffer</c> tagged with the publisher's PeerId in
/// <see cref="VicoScreenShare.Protocol.Messages.SdpOffer.SubscriptionId"/>.
/// </summary>
public sealed class SfuSubscriberPeer : IAsyncDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly ILogger<SfuSubscriberPeer>? _logger;
    private readonly object _candidateLock = new();
    private readonly List<string> _pendingRemoteCandidates = new();
    private bool _remoteDescriptionApplied;
    private bool _disposed;

    // Per-subscriber forward-rate counters. Every packet we attempt to forward
    // through SendForwardedRtp bumps _packetsForwarded + _bytesForwarded. A
    // background timer logs the delta every 2 s so we can see exactly what
    // bitrate each subscriber is being fed — definitive answer to "is Azure
    // keeping up per viewer?" without needing external packet capture.
    private long _packetsForwarded;
    private long _bytesForwarded;
    private long _prevLoggedPackets;
    private long _prevLoggedBytes;
    private System.Threading.Timer? _rateLogTimer;

    public SfuSubscriberPeer(Guid viewerPeerId, Guid publisherPeerId, IReadOnlyList<RTCIceServer> iceServers, ILogger<SfuSubscriberPeer>? logger = null)
    {
        ViewerPeerId = viewerPeerId;
        PublisherPeerId = publisherPeerId;
        _logger = logger;

        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>(iceServers),
            X_UseRtpFeedbackProfile = true,
        });

        // SendOnly track matched by a RecvOnly track on the viewer side. The
        // capability list mirrors SfuPeer so whichever codec the publisher
        // negotiated on their upstream PC survives the re-encode-free fan-out.
        var videoCapabilities = new List<SDPAudioVideoMediaFormat>
        {
            new(new VideoFormat(VideoCodecsEnum.VP8, 96)),
            new(new VideoFormat(VideoCodecsEnum.H264, 102)),
        };
        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: videoCapabilities,
            streamStatus: MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(videoTrack);

        // Opus SendOnly track mirrored with the RecvOnly on the viewer's
        // SubscriberSession. SfuSession.ForwardPublisherRtp already
        // forwards audio packets through SendForwardedRtp regardless of
        // media type, so once this track is here the publisher's audio
        // reaches the viewer automatically.
        var audioCapabilities = new List<SDPAudioVideoMediaFormat>
        {
            new(AudioCommonlyUsedFormats.OpusWebRTC),
        };
        var audioTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.audio,
            isRemote: false,
            capabilities: audioCapabilities,
            streamStatus: MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(audioTrack);

        _pc.onicecandidate += OnLocalIceCandidate;
        _pc.onconnectionstatechange += OnConnectionStateChange;
        _pc.OnReceiveReport += OnViewerReceiveReport;

        // Log per-subscriber send rate every 30 s. Useful for ops ("is this
        // subscriber still being fed at expected bitrate?") without the log
        // spam of a 2-second tick. Quiet entirely when the connection goes
        // idle (no packets since last tick).
        _rateLogTimer = new System.Threading.Timer(
            _ => LogForwardRate(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void LogForwardRate()
    {
        if (_disposed)
        {
            return;
        }
        var pkts = System.Threading.Interlocked.Read(ref _packetsForwarded);
        var bytes = System.Threading.Interlocked.Read(ref _bytesForwarded);
        var dPkts = pkts - _prevLoggedPackets;
        var dBytes = bytes - _prevLoggedBytes;
        _prevLoggedPackets = pkts;
        _prevLoggedBytes = bytes;
        if (dPkts == 0)
        {
            return;
        }
        const double WindowSeconds = 2.0;
        var mbps = dBytes * 8.0 / WindowSeconds / 1_000_000.0;
        var pps = dPkts / WindowSeconds;
        _logger?.LogInformation(
            "[sfu-send] viewer={Viewer} pub={Pub} 2s: {Pps:F0} pps, {Mbps:F2} Mbps | totals pkts={TotalPkts}",
            ViewerPeerId.ToString("N").Substring(0, 8),
            PublisherPeerId.ToString("N").Substring(0, 8),
            pps, mbps, pkts);
    }

    public Guid ViewerPeerId { get; }
    public Guid PublisherPeerId { get; }
    public RTCPeerConnection PeerConnection => _pc;

    /// <summary>
    /// Latest fraction-lost value the viewer reported for this subscriber's
    /// video stream (RFC 3550 byte, 0 = no loss, 255 ≈ 100% loss). Set from
    /// the RTCP Receiver Report handler; read by the SFU's aggregator when
    /// synthesizing a <c>DownstreamLossReport</c> for the upstream publisher.
    /// Zero until the first RR arrives.
    /// </summary>
    public byte LatestFractionLost { get; private set; }

    /// <summary>Subscription id used in protocol messages — the publisher's PeerId in N-format.</summary>
    public string SubscriptionId => PublisherPeerId.ToString("N");

    /// <summary>Fired whenever the subscriber PC gathers a local ICE candidate for the viewer.</summary>
    public event Action<string>? LocalIceCandidateReady;

    /// <summary>Fires once when the subscriber PC reaches the Connected state,
    /// i.e. ICE + DTLS are up and media is now traversable. Used to trigger a
    /// keyframe request upstream so the viewer doesn't wait up to a full GOP
    /// for the next natural IDR.</summary>
    public event Action? Connected;

    // Video forwarding gate. New subscribers can't decode mid-stream
    // P-frames — the AV1 NVDEC parser fires a bogus-dimensions sequence
    // callback before the first real Sequence Header arrives, wedging the
    // decoder for 5+ seconds while the playout queue stays drained. Same
    // class of problem on H.264 (decoder needs SPS+PPS+IDR to initialize).
    //
    // Production SFUs (mediasoup, livekit, Janus) handle this by holding
    // video packets for a fresh subscriber until they observe a keyframe
    // boundary in the publisher's stream, then releasing the gate. The
    // existing sub.Connected → publisher.SendRequestKeyframeAsync chain
    // guarantees the publisher emits an IDR within ~50 ms RTT, so the
    // gate's hold window is short.
    //
    // AV1 keyframe detection: RFC 9066 aggregation header bit 3 is the
    // N flag — set on the first packet of a TU containing a Sequence
    // Header. Our Av1RtpPacketizer sets it (Av1RtpPacketizer.cs:53-64).
    // For H.264 the same bit position falls on FU-A / STAP-A / PPS NAL
    // type bytes which are present in most non-trivial H.264 RTP packets,
    // so the gate effectively releases on the first packet of any
    // multi-NAL frame — fine, since H.264's MFT decoder didn't show the
    // bogus-seq pathology AV1 has.
    //
    // Audio bypasses the gate entirely; Opus is loss-tolerant.
    private int _videoGated = 1; // 1 = gated, 0 = released
    private long _videoPacketsDroppedAtGate;

    /// <summary>
    /// Forward a received RTP packet from the upstream publisher out to the viewer.
    /// Called by <see cref="SfuSession"/> for every packet that the paired
    /// <see cref="SfuPeer"/> receives from the publisher. Video packets are
    /// dropped on the floor until we observe a keyframe-marked packet —
    /// see comment block above for the motivation.
    /// </summary>
    public void SendForwardedRtp(SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (_disposed)
        {
            return;
        }

        if (mediaType == SDPMediaTypesEnum.video
            && System.Threading.Volatile.Read(ref _videoGated) != 0)
        {
            // Codec-aware keyframe detection on the first byte:
            //
            //   PT 102 (AV1 OR H.264, both ride this slot in our SDP) —
            //   bit 3 set means: AV1 RFC 9066 N bit (Sequence Header
            //   start, true keyframe boundary), OR H.264 NAL types
            //   24/28 (STAP-A / FU-A) which fragmented IDRs use. In
            //   production, IDRs are large enough to require FU-A
            //   wrapping, so the first IDR packet has bit 3 set; for
            //   AV1 the N bit is exactly the right signal.
            //
            //   PT 96 (VP8) — first byte is the VP8 payload descriptor
            //   (X|R|N|S|PartID). Bit 3 is part of PartID, not a
            //   keyframe marker. The gate would never release for VP8
            //   without a different signal, so just skip the gate for
            //   VP8 and accept whatever the decoder does on its own.
            //   VP8 doesn't show the AV1 NVDEC bogus-sequence pathology.
            //
            //   Other PTs (audio, RTX) — already filtered above.
            var pt = rtpPacket.Header.PayloadType;
            if (pt != 102)
            {
                System.Threading.Volatile.Write(ref _videoGated, 0);
            }
            else
            {
                var payload = rtpPacket.Payload;
                if (payload is { Length: > 0 } && (payload[0] & 0x08) != 0)
                {
                    System.Threading.Volatile.Write(ref _videoGated, 0);
                    _logger?.LogInformation(
                        "[sfu-gate] viewer={Viewer} pub={Pub} keyframe seen, releasing gate (dropped={Dropped} pkts during gate)",
                        ViewerPeerId, PublisherPeerId,
                        System.Threading.Interlocked.Read(ref _videoPacketsDroppedAtGate));
                }
                else
                {
                    System.Threading.Interlocked.Increment(ref _videoPacketsDroppedAtGate);
                    return;
                }
            }
        }

        try
        {
            _pc.SendRtpRaw(
                mediaType,
                rtpPacket.Payload,
                rtpPacket.Header.Timestamp,
                rtpPacket.Header.MarkerBit,
                rtpPacket.Header.PayloadType);
            System.Threading.Interlocked.Increment(ref _packetsForwarded);
            System.Threading.Interlocked.Add(ref _bytesForwarded, rtpPacket.Payload?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SfuSubscriberPeer {Viewer} <- {Publisher} failed to forward RTP",
                ViewerPeerId, PublisherPeerId);
        }
    }

    /// <summary>
    /// Create the initial SDP offer for this subscriber PC. The server drives the
    /// handshake here — the viewer will answer with a RecvOnly SDP.
    /// </summary>
    public async Task<string> CreateOfferAsync()
    {
        var offer = _pc.createOffer(null);
        await _pc.setLocalDescription(offer).ConfigureAwait(false);
        // Subscriber peer's RTP channel is bound now. This is the egress
        // path the publisher's "down" loss readout is measuring — raising
        // the kernel send buffer here is the most direct thing we can do
        // to absorb fan-out bursts before they're dropped on the wire.
        RtpSocketTuning.TryApply(_pc, msg => _logger?.LogInformation("{Msg}", msg));
        return offer.sdp;
    }

    /// <summary>Apply the viewer's SDP answer.</summary>
    public void HandleRemoteAnswer(string answerSdp)
    {
        var remote = new RTCSessionDescriptionInit
        {
            sdp = answerSdp,
            type = RTCSdpType.answer,
        };
        var setResult = _pc.setRemoteDescription(remote);
        if (setResult != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"setRemoteDescription failed: {setResult}");
        }
        FlushPendingRemoteCandidates();
    }

    public void AddRemoteIceCandidate(string candidateJson)
    {
        if (string.IsNullOrWhiteSpace(candidateJson))
        {
            return;
        }

        lock (_candidateLock)
        {
            if (!_remoteDescriptionApplied)
            {
                _pendingRemoteCandidates.Add(candidateJson);
                return;
            }
        }

        ApplyRemoteCandidateInternal(candidateJson);
    }

    private void OnViewerReceiveReport(System.Net.IPEndPoint endpoint, SDPMediaTypesEnum mediaType, RTCPCompoundPacket report)
    {
        if (_disposed)
        {
            return;
        }
        if (mediaType != SDPMediaTypesEnum.video)
        {
            return;
        }
        var rr = report.ReceiverReport;
        if (rr?.ReceptionReports is not null)
        {
            byte worst = 0;
            foreach (var sample in rr.ReceptionReports)
            {
                if (sample.FractionLost > worst)
                {
                    worst = sample.FractionLost;
                }
            }
            LatestFractionLost = worst;
        }

        // Look for a PLI feedback packet from this viewer. SIPSorcery exposes
        // the feedback subtype on RTCPFeedback.Header — PSFB-class packets
        // (PacketType == PSFB) carry the picture-specific feedback subtype
        // in PayloadFeedbackMessageType (PLI is the one we care about).
        // RTPFB-class packets (NACK / TWCC) use FeedbackMessageType instead;
        // we don't act on those here. RTCPCompoundPacket.Feedback is the
        // single RTCPFeedback present in the compound (or null).
        var fb = report.Feedback;
        if (fb is not null
            && fb.Header.PacketType == RTCPReportTypesEnum.PSFB
            && fb.Header.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.PLI)
        {
            try
            {
                ViewerKeyframeRequested?.Invoke();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SfuSubscriberPeer {Viewer}<-{Publisher} ViewerKeyframeRequested handler threw",
                    ViewerPeerId, PublisherPeerId);
            }
        }
    }

    /// <summary>
    /// Fires when this subscriber's viewer-side RTCPeerConnection sends a
    /// PLI or FIR. <see cref="SfuSession"/> wires this to the matching
    /// publisher's <see cref="SfuPeer.TryForwardPli"/>, which coalesces
    /// across viewers and emits a single upstream PLI per publisher.
    /// </summary>
    public event Action? ViewerKeyframeRequested;

    private void OnLocalIceCandidate(RTCIceCandidate candidate)
    {
        try
        {
            LocalIceCandidateReady?.Invoke(candidate.toJSON());
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SfuSubscriberPeer {Viewer}<-{Publisher} failed to serialize local ICE candidate",
                ViewerPeerId, PublisherPeerId);
        }
    }

    private bool _connectedFired;

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        _logger?.LogInformation("SfuSubscriberPeer {Viewer}<-{Publisher} state: {State}",
            ViewerPeerId, PublisherPeerId, state);

        if (state == RTCPeerConnectionState.connected && !_connectedFired)
        {
            _connectedFired = true;
            try
            {
                Connected?.Invoke();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SfuSubscriberPeer {Viewer}<-{Publisher} Connected handler threw",
                    ViewerPeerId, PublisherPeerId);
            }
        }
    }

    private void FlushPendingRemoteCandidates()
    {
        List<string> toFlush;
        lock (_candidateLock)
        {
            _remoteDescriptionApplied = true;
            if (_pendingRemoteCandidates.Count == 0)
            {
                return;
            }

            toFlush = new List<string>(_pendingRemoteCandidates);
            _pendingRemoteCandidates.Clear();
        }

        foreach (var candidate in toFlush)
        {
            ApplyRemoteCandidateInternal(candidate);
        }
    }

    private void ApplyRemoteCandidateInternal(string candidateJson)
    {
        try
        {
            var init = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidateJson);
            if (init is not null)
            {
                _pc.addIceCandidate(init);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SfuSubscriberPeer {Viewer}<-{Publisher} failed to add remote ICE candidate",
                ViewerPeerId, PublisherPeerId);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        try { _rateLogTimer?.Dispose(); } catch { }
        _rateLogTimer = null;
        try { _pc.onicecandidate -= OnLocalIceCandidate; } catch { }
        try { _pc.onconnectionstatechange -= OnConnectionStateChange; } catch { }
        try { _pc.close(); } catch { }
        try { _pc.Dispose(); } catch { }

        return ValueTask.CompletedTask;
    }
}
