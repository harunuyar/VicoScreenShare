namespace VicoScreenShare.Client.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Protocol.Messages;

/// <summary>
/// Client-side wrapper around a single <see cref="RTCPeerConnection"/> plus the
/// signaling glue to negotiate it with the server SFU. One instance per stream:
/// the streamer owns a sender session, each viewer owns its own receiver session.
/// </summary>
public sealed class WebRtcSession : IAsyncDisposable
{
    private readonly SignalingClient _signaling;
    private readonly WebRtcRole _role;
    private readonly RTCPeerConnection _pc;
    private readonly object _candidateLock = new();
    private readonly List<string> _pendingRemoteCandidates = new();
    private bool _remoteDescriptionApplied;
    private bool _disposed;

    public WebRtcSession(SignalingClient signaling, WebRtcRole role)
        : this(signaling, role, VideoCodec.Vp8, iceServers: null)
    {
    }

    public WebRtcSession(SignalingClient signaling, WebRtcRole role, VideoCodec codec)
        : this(signaling, role, codec, iceServers: null)
    {
    }

    public WebRtcSession(
        SignalingClient signaling,
        WebRtcRole role,
        VideoCodec codec,
        IReadOnlyList<RTCIceServer>? iceServers)
    {
        _signaling = signaling;
        _role = role;

        // The room advertises its ICE servers via RoomJoined.IceServers;
        // callers that join a room pass the list here. Null or empty =
        // fall back to Google's public STUN so tests and stand-alone
        // harnesses that bypass the server still get NAT traversal.
        var servers = iceServers is { Count: > 0 }
            ? new List<RTCIceServer>(iceServers)
            : new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } };

        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = servers,
            X_UseRtpFeedbackProfile = true,
        });

        // Advertise the preferred codec first, followed by the other as a
        // universal fallback. WebRTC codec selection is driven by intersection
        // order, so listing the user's pick at index 0 means both peers agree
        // on it whenever both sides support it.
        //
        // CRITICAL: The payload-type assignment MUST match the server's
        // convention (VP8=96, H264=102). Server-side SfuPeer + SfuSubscriberPeer
        // use those fixed PTs; when the server forwards a publisher's RTP
        // byte-for-byte to a subscriber, it copies the ORIGINAL payload type
        // from the inbound packet. If the publisher negotiated H264=96 here
        // but the subscriber PC has 96 mapped to VP8, the subscriber decodes
        // the bytes as the wrong codec and frames never produce output.
        var preferred = MapCodec(codec);
        var capabilities = new List<SDPAudioVideoMediaFormat>();
        if (preferred == VideoCodecsEnum.VP8)
        {
            capabilities.Add(new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.VP8, 96)));
            capabilities.Add(new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H264, 102)));
        }
        else
        {
            capabilities.Add(new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H264, 102)));
            capabilities.Add(new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.VP8, 96)));
        }

        var direction = role switch
        {
            WebRtcRole.Sender => MediaStreamStatusEnum.SendOnly,
            WebRtcRole.Receiver => MediaStreamStatusEnum.RecvOnly,
            WebRtcRole.Bidirectional => MediaStreamStatusEnum.SendRecv,
            _ => MediaStreamStatusEnum.SendRecv,
        };

        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: capabilities,
            streamStatus: direction);
        _pc.addTrack(videoTrack);

        // Shared-content audio track. Opus at PT 111, 48 kHz stereo — the
        // WebRTC canonical parameters, also what the SFU advertises on
        // both SfuPeer (ingest) and SfuSubscriberPeer (egress). The track
        // is always negotiated so enabling / disabling audio capture at
        // runtime never forces an SDP renegotiation that would glitch
        // the live video stream; the AudioStreamer controls whether
        // bytes actually flow via SendAudio.
        var audioTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.audio,
            isRemote: false,
            capabilities: new List<SDPAudioVideoMediaFormat> { new(AudioCommonlyUsedFormats.OpusWebRTC) },
            streamStatus: direction);
        _pc.addTrack(audioTrack);

        _pc.onicecandidate += OnLocalIceCandidate;
        _signaling.IceCandidateReceived += OnRemoteIceCandidate;
    }

    public RTCPeerConnection PeerConnection => _pc;

    public RTCPeerConnectionState ConnectionState => _pc.connectionState;

    public event Action<RTCPeerConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Initiate the handshake: create an offer, send it to the server, wait for
    /// an answer to come back via the signaling channel, and set the remote
    /// description. Returns when setRemoteDescription has accepted the answer.
    /// </summary>
    public async Task NegotiateAsync(TimeSpan? answerTimeout = null)
    {
        var timeout = answerTimeout ?? TimeSpan.FromSeconds(10);

        var answerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAnswer(SdpAnswer a)
        {
            // Only the main-PC answer completes this task; subscriber-PC answers
            // never flow back to the client (client is the answerer there).
            if (string.IsNullOrEmpty(a.SubscriptionId))
            {
                answerTcs.TrySetResult(a.Sdp);
            }
        }
        _signaling.SdpAnswerReceived += OnAnswer;
        _pc.onconnectionstatechange += OnPcStateChanged;

        try
        {
            var offer = _pc.createOffer(null);
            await _pc.setLocalDescription(offer).ConfigureAwait(false);
            // Immediately after setLocalDescription the RTP channel exists and
            // its UDP socket is bound; this is the earliest point we can raise
            // the kernel receive buffer above the tiny Windows default so a
            // burst on a high-bitrate link does not silently tail-drop.
            RtpSocketTuning.TryApply(_pc);
            await _signaling.SendSdpOfferAsync(offer.sdp).ConfigureAwait(false);

            using var timeoutCts = new System.Threading.CancellationTokenSource(timeout);
            var finished = await Task.WhenAny(answerTcs.Task, Task.Delay(timeout, timeoutCts.Token))
                .ConfigureAwait(false);
            if (finished != answerTcs.Task)
            {
                throw new TimeoutException($"SDP answer did not arrive within {timeout.TotalSeconds:F1}s");
            }

            var answerSdp = answerTcs.Task.Result;
            var setResult = _pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                sdp = answerSdp,
                type = RTCSdpType.answer,
            });
            if (setResult != SetDescriptionResultEnum.OK)
            {
                throw new InvalidOperationException($"setRemoteDescription failed: {setResult}");
            }

            FlushPendingRemoteCandidates();
        }
        finally
        {
            _signaling.SdpAnswerReceived -= OnAnswer;
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

    private void OnLocalIceCandidate(RTCIceCandidate candidate)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var json = candidate.toJSON();
            _ = _signaling.SendIceCandidateAsync(json);
        }
        catch { /* best-effort during teardown */ }
    }

    private void OnRemoteIceCandidate(IceCandidate ice)
    {
        if (_disposed || ice is null || string.IsNullOrWhiteSpace(ice.Candidate))
        {
            return;
        }

        // Main PC only consumes candidates tagged for the main PC (null id).
        if (!string.IsNullOrEmpty(ice.SubscriptionId))
        {
            return;
        }

        lock (_candidateLock)
        {
            if (!_remoteDescriptionApplied)
            {
                // Trickled server candidate arrived before NegotiateAsync applied
                // setRemoteDescription(answer); buffer and flush after.
                _pendingRemoteCandidates.Add(ice.Candidate);
                return;
            }
        }

        ApplyRemoteCandidateInternal(ice.Candidate);
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
        catch { /* ignore malformed candidates */ }
    }

    /// <summary>Exposed for tests so they can assert the buffer drained as expected.</summary>
    internal int PendingRemoteCandidateCount
    {
        get { lock (_candidateLock)
            {
                return _pendingRemoteCandidates.Count;
            }
        }
    }

    private void OnPcStateChanged(RTCPeerConnectionState state)
    {
        ConnectionStateChanged?.Invoke(state);
    }

    private static VideoCodecsEnum MapCodec(VideoCodec codec) => codec switch
    {
        VideoCodec.Vp8 => VideoCodecsEnum.VP8,
        VideoCodec.H264 => VideoCodecsEnum.H264,
        // AV1 is not in SIPSorcery's enum as of 10.0.3 — fall back to H.264 at
        // the SDP layer even if the user picked AV1, so we never end up with
        // no negotiated codec at all. The factory layer will show AV1 as
        // unavailable on machines without support anyway.
        VideoCodec.Av1 => VideoCodecsEnum.H264,
        _ => VideoCodecsEnum.VP8,
    };

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        _signaling.IceCandidateReceived -= OnRemoteIceCandidate;
        try { _pc.onicecandidate -= OnLocalIceCandidate; } catch { }
        try { _pc.onconnectionstatechange -= OnPcStateChanged; } catch { }
        try { _pc.close(); } catch { }
        try { _pc.Dispose(); } catch { }

        return ValueTask.CompletedTask;
    }
}

public enum WebRtcRole
{
    /// <summary>The client will send media to the server (streamer).</summary>
    Sender,

    /// <summary>The client will receive media from the server (viewer).</summary>
    Receiver,

    /// <summary>
    /// The client will both send and receive media through a single
    /// <see cref="RTCPeerConnection"/>. Used by the room view so one signalling
    /// session handles both the outbound screen share and any forwarded streams
    /// from other peers in the same room.
    /// </summary>
    Bidirectional,
}
