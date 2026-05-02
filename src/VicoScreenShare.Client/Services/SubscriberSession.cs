namespace VicoScreenShare.Client.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;
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

    /// <summary>
    /// Shared-content audio sink, if the host registered an audio
    /// decoder factory + renderer. Null when audio isn't available on
    /// this host (e.g. a headless benchmark harness), in which case
    /// audio RTP packets are silently dropped at the SIPSorcery layer.
    /// </summary>
    public AudioReceiver? AudioReceiver { get; }

    /// <summary>
    /// Per-publisher A/V sync clock when sync is enabled. Both
    /// <see cref="Receiver"/> and <see cref="AudioReceiver"/>
    /// reference the same instance so audio playout aligns to
    /// video's effective wall-clock schedule. Null when the session
    /// was constructed with <c>enableAvSync: false</c>.
    /// </summary>
    public MediaClock? MediaClock { get; }

    public RTCPeerConnection PeerConnection => _pc;

    public SubscriberSession(
        SignalingClient signaling,
        Guid publisherPeerId,
        IVideoDecoderFactory decoderFactory,
        string displayName)
        : this(signaling, publisherPeerId, decoderFactory, displayName, iceServers: null, audioDecoderFactory: null, audioRenderer: null, enableAvSync: true)
    {
    }

    public SubscriberSession(
        SignalingClient signaling,
        Guid publisherPeerId,
        IVideoDecoderFactory decoderFactory,
        string displayName,
        IReadOnlyList<RTCIceServer>? iceServers)
        : this(signaling, publisherPeerId, decoderFactory, displayName, iceServers, audioDecoderFactory: null, audioRenderer: null, enableAvSync: true)
    {
    }

    public SubscriberSession(
        SignalingClient signaling,
        Guid publisherPeerId,
        IVideoDecoderFactory decoderFactory,
        string displayName,
        IReadOnlyList<RTCIceServer>? iceServers,
        IAudioDecoderFactory? audioDecoderFactory,
        IAudioRenderer? audioRenderer)
        : this(signaling, publisherPeerId, decoderFactory, displayName, iceServers, audioDecoderFactory, audioRenderer, enableAvSync: true)
    {
    }

    public SubscriberSession(
        SignalingClient signaling,
        Guid publisherPeerId,
        IVideoDecoderFactory decoderFactory,
        string displayName,
        IReadOnlyList<RTCIceServer>? iceServers,
        IAudioDecoderFactory? audioDecoderFactory,
        IAudioRenderer? audioRenderer,
        int videoWidth,
        int videoHeight)
        : this(signaling, publisherPeerId, decoderFactory, displayName, iceServers, audioDecoderFactory, audioRenderer, enableAvSync: true, videoWidth: videoWidth, videoHeight: videoHeight)
    {
    }

    /// <summary>
    /// Build a subscriber session with explicit control over the
    /// MediaClock-based A/V sync. Set <paramref name="enableAvSync"/>
    /// to <see langword="false"/> for sessions that won't have a
    /// video stream attached (audio-only loopback, headless tests),
    /// which would otherwise hold audio forever waiting for a video
    /// paint that never comes. Production callers always pass
    /// <see langword="true"/> — there is a real renderer attached
    /// that will publish the anchor.
    /// </summary>
    public SubscriberSession(
        SignalingClient signaling,
        Guid publisherPeerId,
        IVideoDecoderFactory decoderFactory,
        string displayName,
        IReadOnlyList<RTCIceServer>? iceServers,
        IAudioDecoderFactory? audioDecoderFactory,
        IAudioRenderer? audioRenderer,
        bool enableAvSync,
        int videoWidth = 0,
        int videoHeight = 0)
    {
        _signaling = signaling;
        PublisherPeerId = publisherPeerId;
        SubscriptionId = publisherPeerId.ToString("N");

        // Use server-provided ICE servers when the room supplied them; fall
        // back to Google's public STUN so test harnesses that don't join a
        // real room still get NAT traversal.
        var servers = iceServers is { Count: > 0 }
            ? new List<RTCIceServer>(iceServers)
            : new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } };

        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = servers,
            X_UseRtpFeedbackProfile = true,
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

        // Opus RecvOnly track. The server's SfuSubscriberPeer sends on
        // audio PT 111; this PC must advertise the matching format so
        // the m=audio section intersects and audio RTP actually lands.
        var audioTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.audio,
            isRemote: false,
            capabilities: new List<SDPAudioVideoMediaFormat> { new(AudioCommonlyUsedFormats.OpusWebRTC) },
            streamStatus: MediaStreamStatusEnum.RecvOnly);
        _pc.addTrack(audioTrack);

        _pc.onicecandidate += OnLocalIceCandidate;
        _signaling.IceCandidateReceived += OnRemoteIceCandidate;

        // Diagnostic: log every PC state transition so we can see whether
        // SDP/ICE actually completes after a codec-rebuild restart.
        _pc.onconnectionstatechange += s => DebugLog.Write(
            $"[sub {SubscriptionId:N}] connectionState={s} ice={_pc.iceConnectionState} signaling={_pc.signalingState}");
        _pc.oniceconnectionstatechange += s => DebugLog.Write(
            $"[sub {SubscriptionId:N}] iceConnectionState={s}");

        // Per-subscriber A/V sync clock. Owned here so both
        // StreamReceiver and AudioReceiver hold the same instance —
        // they latch and query through it to keep audio playout
        // aligned with video's content-PTS pacer (PaintLoop).
        // Disabled (null) for sessions that don't have a real video
        // renderer to set the anchor (audio-only, tests).
        MediaClock = enableAvSync ? new MediaClock(displayName) : null;

        Receiver = new StreamReceiver(_pc, decoderFactory, displayName, MediaClock, videoWidth, videoHeight);

        // AudioReceiver is a sibling (not a layer inside StreamReceiver)
        // so the video receiver's bounded-channel / drop-to-IDR / GPU
        // texture path stays specialized to video. If either the
        // factory or the renderer is null the viewer simply doesn't
        // play shared-content audio for this publisher — the RTP still
        // reaches SIPSorcery but OnRtpPacketReceived ignores audio
        // packets (StreamReceiver filters by mediaType == video).
        if (audioDecoderFactory is not null && audioRenderer is not null)
        {
            AudioReceiver = new AudioReceiver(_pc, audioDecoderFactory, audioRenderer, displayName, MediaClock);
        }

        // Route incoming RTCP Sender Reports into the shared media
        // clock. SIPSorcery raises OnReceiveReport for every compound
        // packet (RR or SR) on the matching media type; we only care
        // about SRs because they carry the (NTP, RTP) pair we need
        // for cross-stream timestamp translation.
        if (MediaClock is not null)
        {
            _pc.OnReceiveReport += OnReceiveReport;
        }
    }

    private void OnReceiveReport(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTCPCompoundPacket report)
    {
        if (_disposed)
        {
            return;
        }
        var sr = report?.SenderReport;
        if (sr is null)
        {
            return;
        }
        var clock = MediaClock;
        if (clock is null)
        {
            return;
        }
        try
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                clock.OnAudioSenderReport(sr.NtpTimestamp, sr.RtpTimestamp);
            }
            else if (mediaType == SDPMediaTypesEnum.video)
            {
                clock.OnVideoSenderReport(sr.NtpTimestamp, sr.RtpTimestamp);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[subscriber] OnReceiveReport handler threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply a server-sent <see cref="SdpOffer"/> for this subscription, create
    /// the matching answer, set local description, and ship the answer back.
    /// Caller must only invoke for offers whose SubscriptionId matches
    /// <see cref="SubscriptionId"/>.
    /// </summary>
    public async Task AcceptOfferAsync(string offerSdp)
    {
        DebugLog.Write($"[sub {SubscriptionId:N}] AcceptOfferAsync entry — offer length={offerSdp?.Length ?? 0}");
        var remote = new RTCSessionDescriptionInit
        {
            sdp = offerSdp,
            type = RTCSdpType.offer,
        };
        var setResult = _pc.setRemoteDescription(remote);
        DebugLog.Write($"[sub {SubscriptionId:N}] setRemoteDescription -> {setResult}");
        if (setResult != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"setRemoteDescription failed: {setResult}");
        }

        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer).ConfigureAwait(false);
        DebugLog.Write($"[sub {SubscriptionId:N}] setLocalDescription done — answer length={answer.sdp?.Length ?? 0} iceState={_pc.iceConnectionState} pcState={_pc.connectionState}");
        // Raise the kernel UDP receive buffer on this subscriber PC's RTP
        // socket before any media arrives. Three subscribers on the same box
        // receiving ~20 Mbit each easily fill a 64 KB default buffer mid-burst,
        // producing the packet-loss-driven distortion we see on fat pipes.
        RtpSocketTuning.TryApply(_pc);

        await _signaling.SendSdpAnswerAsync(answer.sdp, SubscriptionId).ConfigureAwait(false);
        DebugLog.Write($"[sub {SubscriptionId:N}] sent SdpAnswer to signaling");

        FlushPendingRemoteCandidates();

        // Start the receiver so OnVideoFrameReceived flows through the decoder
        // into the viewer's renderer as soon as the first RTP arrives.
        await Receiver.StartAsync().ConfigureAwait(false);

        // AudioReceiver subscribes to OnRtpPacketReceived so it must
        // start after the PC is negotiated; do it now rather than at
        // construction to mirror the video receiver's lifecycle.
        if (AudioReceiver is not null)
        {
            await AudioReceiver.StartAsync().ConfigureAwait(false);
        }
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
        catch (ObjectDisposedException)
        {
            // Race with Dispose. Expected, not a real abnormality.
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[sub {SubscriptionId:N}] OnLocalIceCandidate send threw: {ex.GetType().Name}: {ex.Message}");
        }
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
        catch (ObjectDisposedException)
        {
            // Race with Dispose. Expected, not a real abnormality.
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[sub {SubscriptionId:N}] addIceCandidate threw: {ex.GetType().Name}: {ex.Message} (candidate={candidateJson})");
        }
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
        try { _pc.OnReceiveReport -= OnReceiveReport; } catch { }

        try { await Receiver.DisposeAsync().ConfigureAwait(false); } catch { }
        if (AudioReceiver is not null)
        {
            try { await AudioReceiver.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        try { _pc.close(); } catch { }
        try { _pc.Dispose(); } catch { }
    }
}
