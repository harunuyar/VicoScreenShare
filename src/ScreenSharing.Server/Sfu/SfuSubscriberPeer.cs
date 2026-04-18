using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ScreenSharing.Server.Sfu;

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
/// <see cref="ScreenSharing.Protocol.Messages.SdpOffer.SubscriptionId"/>.
/// </summary>
public sealed class SfuSubscriberPeer : IAsyncDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly ILogger<SfuSubscriberPeer>? _logger;
    private readonly object _candidateLock = new();
    private readonly List<string> _pendingRemoteCandidates = new();
    private bool _remoteDescriptionApplied;
    private bool _disposed;

    public SfuSubscriberPeer(Guid viewerPeerId, Guid publisherPeerId, ILogger<SfuSubscriberPeer>? logger = null)
    {
        ViewerPeerId = viewerPeerId;
        PublisherPeerId = publisherPeerId;
        _logger = logger;

        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = "stun:stun.l.google.com:19302" },
            },
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

        _pc.onicecandidate += OnLocalIceCandidate;
        _pc.onconnectionstatechange += OnConnectionStateChange;
    }

    public Guid ViewerPeerId { get; }
    public Guid PublisherPeerId { get; }
    public RTCPeerConnection PeerConnection => _pc;

    /// <summary>Subscription id used in protocol messages — the publisher's PeerId in N-format.</summary>
    public string SubscriptionId => PublisherPeerId.ToString("N");

    /// <summary>Fired whenever the subscriber PC gathers a local ICE candidate for the viewer.</summary>
    public event Action<string>? LocalIceCandidateReady;

    /// <summary>
    /// Forward a received RTP packet from the upstream publisher out to the viewer.
    /// Called by <see cref="SfuSession"/> for every packet that the paired
    /// <see cref="SfuPeer"/> receives from the publisher.
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

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        _logger?.LogInformation("SfuSubscriberPeer {Viewer}<-{Publisher} state: {State}",
            ViewerPeerId, PublisherPeerId, state);
    }

    private void FlushPendingRemoteCandidates()
    {
        List<string> toFlush;
        lock (_candidateLock)
        {
            _remoteDescriptionApplied = true;
            if (_pendingRemoteCandidates.Count == 0) return;
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
            if (init is not null) _pc.addIceCandidate(init);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SfuSubscriberPeer {Viewer}<-{Publisher} failed to add remote ICE candidate",
                ViewerPeerId, PublisherPeerId);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        try { _pc.onicecandidate -= OnLocalIceCandidate; } catch { }
        try { _pc.onconnectionstatechange -= OnConnectionStateChange; } catch { }
        try { _pc.close(); } catch { }
        try { _pc.Dispose(); } catch { }

        return ValueTask.CompletedTask;
    }
}
