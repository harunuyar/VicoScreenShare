using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace VicoScreenShare.Server.Sfu;

/// <summary>
/// One server-side <see cref="RTCPeerConnection"/> paired to a client. Handles the
/// offer / answer / ICE handshake and exposes hooks for a future <c>RtpForwarder</c>
/// to subscribe to received RTP. Does not create tracks on its own — the server
/// accepts whatever tracks the client's offer proposes and mirrors them back in
/// the answer via <see cref="RTCPeerConnection.createAnswer"/>.
/// </summary>
public sealed class SfuPeer : IAsyncDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly ILogger<SfuPeer>? _logger;
    private readonly object _candidateLock = new();
    private readonly List<string> _pendingRemoteCandidates = new();
    private bool _remoteDescriptionApplied;
    private bool _disposed;

    public SfuPeer(Guid peerId, ILogger<SfuPeer>? logger = null)
    {
        PeerId = peerId;
        _logger = logger;

        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = "stun:stun.l.google.com:19302" },
            },
        });

        // The main SfuPeer PC is now RecvOnly from the server's perspective.
        // It receives the client's outbound publish stream, nothing else —
        // fan-out to viewers happens on dedicated SfuSubscriberPeer PCs (one
        // per (viewer, publisher) pair), which carry their own SSRCs so the
        // viewer can demux naturally without multi-track SDP gymnastics or
        // dynamic renegotiation. The client's offer will still be SendRecv or
        // SendOnly; RecvOnly on our side is the correct matching direction.
        //
        // Advertise ALL codecs the clients might pick (VP8 baseline and H.264
        // via Media Foundation on Windows) so the SDP intersection always has
        // at least one format in common regardless of which codec the client
        // chose in its settings.
        var videoCapabilities = new List<SDPAudioVideoMediaFormat>
        {
            new(new VideoFormat(VideoCodecsEnum.VP8, 96)),
            new(new VideoFormat(VideoCodecsEnum.H264, 102)),
        };
        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: videoCapabilities,
            streamStatus: MediaStreamStatusEnum.RecvOnly);
        _pc.addTrack(videoTrack);

        _pc.onicecandidate += OnLocalIceCandidate;
        _pc.onconnectionstatechange += OnConnectionStateChange;
        _pc.OnRtpPacketReceived += OnRtpPacketReceived;
    }

    public Guid PeerId { get; }

    public RTCPeerConnection PeerConnection => _pc;

    /// <summary>
    /// Fired whenever the server-side peer connection gathers a new ICE candidate.
    /// The payload is the JSON form produced by SIPSorcery, suitable for putting
    /// straight into <c>IceCandidate.Candidate</c> so the client can feed it back
    /// into its own peer connection.
    /// </summary>
    public event Action<string>? LocalIceCandidateReady;

    /// <summary>
    /// Fired for every RTP packet received from the remote peer. Consumed by the
    /// <see cref="SfuSession"/> RTP forwarder to fan the packet out to every
    /// other peer in the same room.
    /// </summary>
    public event Action<SDPMediaTypesEnum, RTPPacket>? RtpPacketReceived;

    private void OnRtpPacketReceived(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        try
        {
            RtpPacketReceived?.Invoke(mediaType, rtpPacket);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SfuPeer {PeerId} RTP forwarder handler threw", PeerId);
        }
    }

    /// <summary>
    /// Forward an RTP packet we received from another peer out through this peer's
    /// send track. The receiver sees it as a normal stream; SIPSorcery's
    /// <c>SendRtpRaw</c> assigns its own SSRC and sequence number for this outbound
    /// path, which is exactly the SFU fan-out behavior we want.
    /// </summary>
    public void SendForwardedRtp(SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (_disposed) return;
        try
        {
            _pc.SendRtpRaw(
                mediaType,
                rtpPacket.Payload,
                rtpPacket.Header.Timestamp,
                rtpPacket.Header.MarkerBit,
                rtpPacket.Header.PayloadType);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SfuPeer {PeerId} failed to forward RTP", PeerId);
        }
    }

    private void OnLocalIceCandidate(RTCIceCandidate candidate)
    {
        try
        {
            var json = candidate.toJSON();
            LocalIceCandidateReady?.Invoke(json);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SfuPeer {PeerId} failed to serialize local ICE candidate", PeerId);
        }
    }

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        _logger?.LogInformation("SfuPeer {PeerId} connection state: {State}", PeerId, state);
    }

    /// <summary>
    /// Consume a remote offer and return the server's answer SDP. Any ICE
    /// candidates that arrived before the offer are flushed once the remote
    /// description has been applied.
    /// </summary>
    public async Task<string> HandleRemoteOfferAsync(string offerSdp)
    {
        var remote = new RTCSessionDescriptionInit
        {
            sdp = offerSdp,
            type = RTCSdpType.offer,
        };

        var setResult = _pc.setRemoteDescription(remote);
        if (setResult != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"setRemoteDescription failed: {setResult}");
        }

        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer).ConfigureAwait(false);

        FlushPendingRemoteCandidates();
        return answer.sdp;
    }

    /// <summary>
    /// Consume a remote answer (used when the server is the offerer; not in the
    /// main Phase 3.1 flow but kept here so the message dispatcher stays symmetric).
    /// </summary>
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
    }

    /// <summary>
    /// Apply a remote ICE candidate if the peer connection's remote description has
    /// already been set, or buffer it for later replay otherwise. Trickle ICE
    /// candidates on a fast connection routinely race ahead of the SDP round-trip,
    /// and SIPSorcery's <c>addIceCandidate</c> silently drops them if the remote
    /// description has not yet been applied.
    /// </summary>
    public void AddRemoteIceCandidate(string candidateJson)
    {
        if (string.IsNullOrWhiteSpace(candidateJson)) return;

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
            _logger?.LogDebug(ex, "SfuPeer {PeerId} failed to add remote ICE candidate", PeerId);
        }
    }

    /// <summary>Exposed for tests so they can assert the buffer drained as expected.</summary>
    internal int PendingRemoteCandidateCount
    {
        get { lock (_candidateLock) return _pendingRemoteCandidates.Count; }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        try { _pc.onicecandidate -= OnLocalIceCandidate; } catch { }
        try { _pc.onconnectionstatechange -= OnConnectionStateChange; } catch { }
        try { _pc.OnRtpPacketReceived -= OnRtpPacketReceived; } catch { }
        try { _pc.close(); } catch { }
        try { _pc.Dispose(); } catch { }

        return ValueTask.CompletedTask;
    }
}
