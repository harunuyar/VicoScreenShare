namespace VicoScreenShare.Client.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Protocol.Messages;

/// <summary>
/// Client-side RecvOnly peer connection paired with one server
/// <c>SfuSubscriberPeer</c>. Created lazily when the signaling channel delivers
/// an <see cref="SdpOffer"/> whose <see cref="SdpOffer.SubscriptionId"/> matches
/// a publisher the viewer is about to watch. The server is the offerer; the
/// client applies the remote description, creates an answer, and ships it back
/// with the same SubscriptionId. A dedicated <see cref="StreamReceiver"/> feeds
/// decoded frames to whichever UI consumer binds to it.
/// </summary>
public sealed class SubscriberSession : IAsyncDisposable
{
    private readonly SignalingClient _signaling;
    private readonly RTCPeerConnection _pc;
    private readonly object _candidateLock = new();
    private readonly List<string> _pendingRemoteCandidates = new();
    private bool _remoteDescriptionApplied;
    private bool _disposed;

    /// <summary>Wire id of this subscription — the publisher's PeerId in "N" format.</summary>
    public string SubscriptionId { get; }

    /// <summary>Logical publisher this subscription renders.</summary>
    public Guid PublisherPeerId { get; }

    public StreamReceiver Receiver { get; }

    public RTCPeerConnection PeerConnection => _pc;

    public SubscriberSession(
        SignalingClient signaling,
        Guid publisherPeerId,
        IVideoDecoderFactory decoderFactory,
        string displayName)
    {
        _signaling = signaling;
        PublisherPeerId = publisherPeerId;
        SubscriptionId = publisherPeerId.ToString("N");

        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = "stun:stun.l.google.com:19302" },
            },
        });

        // RecvOnly: server is the sender on this PC. Capabilities mirror both
        // halves of the potential negotiated codec set — whichever the server
        // lists in its offer will intersect with one of these.
        var capabilities = new List<SDPAudioVideoMediaFormat>
        {
            new(new VideoFormat(VideoCodecsEnum.VP8, 96)),
            new(new VideoFormat(VideoCodecsEnum.H264, 102)),
        };
        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: capabilities,
            streamStatus: MediaStreamStatusEnum.RecvOnly);
        _pc.addTrack(videoTrack);

        _pc.onicecandidate += OnLocalIceCandidate;
        _signaling.IceCandidateReceived += OnRemoteIceCandidate;

        Receiver = new StreamReceiver(_pc, decoderFactory, displayName);
    }

    /// <summary>
    /// Apply a server-sent <see cref="SdpOffer"/> for this subscription, create
    /// the matching answer, set local description, and ship the answer back.
    /// Caller must only invoke for offers whose SubscriptionId matches
    /// <see cref="SubscriptionId"/>.
    /// </summary>
    public async Task AcceptOfferAsync(string offerSdp)
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

        await _signaling.SendSdpAnswerAsync(answer.sdp, SubscriptionId).ConfigureAwait(false);

        FlushPendingRemoteCandidates();

        // Start the receiver so OnVideoFrameReceived flows through the decoder
        // into the viewer's renderer as soon as the first RTP arrives.
        await Receiver.StartAsync().ConfigureAwait(false);
    }

    private void OnLocalIceCandidate(RTCIceCandidate candidate)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _ = _signaling.SendIceCandidateAsync(candidate.toJSON(), SubscriptionId);
        }
        catch { /* teardown */ }
    }

    private void OnRemoteIceCandidate(IceCandidate ice)
    {
        if (_disposed || ice is null || string.IsNullOrWhiteSpace(ice.Candidate))
        {
            return;
        }

        if (!string.Equals(ice.SubscriptionId, SubscriptionId, StringComparison.Ordinal))
        {
            return;
        }

        lock (_candidateLock)
        {
            if (!_remoteDescriptionApplied)
            {
                _pendingRemoteCandidates.Add(ice.Candidate);
                return;
            }
        }
        ApplyRemoteCandidateInternal(ice.Candidate);
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
        foreach (var c in toFlush)
        {
            ApplyRemoteCandidateInternal(c);
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
        catch { /* ignore malformed */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _signaling.IceCandidateReceived -= OnRemoteIceCandidate;
        try { _pc.onicecandidate -= OnLocalIceCandidate; } catch { }

        try { await Receiver.DisposeAsync().ConfigureAwait(false); } catch { }
        try { _pc.close(); } catch { }
        try { _pc.Dispose(); } catch { }
    }
}
