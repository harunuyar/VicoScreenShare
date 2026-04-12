using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ScreenSharing.Client.Services;

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
    {
        _signaling = signaling;
        _role = role;

        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = "stun:stun.l.google.com:19302" },
            },
        });

        var videoFormat = new VideoFormat(VideoCodecsEnum.VP8, 96);
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
            capabilities: new List<SDPAudioVideoMediaFormat> { new(videoFormat) },
            streamStatus: direction);
        _pc.addTrack(videoTrack);

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
        void OnAnswer(string sdp) => answerTcs.TrySetResult(sdp);
        _signaling.SdpAnswerReceived += OnAnswer;
        _pc.onconnectionstatechange += OnPcStateChanged;

        try
        {
            var offer = _pc.createOffer(null);
            await _pc.setLocalDescription(offer).ConfigureAwait(false);
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
            if (_pendingRemoteCandidates.Count == 0) return;
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
        if (_disposed) return;
        try
        {
            var json = candidate.toJSON();
            _ = _signaling.SendIceCandidateAsync(json);
        }
        catch { /* best-effort during teardown */ }
    }

    private void OnRemoteIceCandidate(string candidateJson)
    {
        if (_disposed || string.IsNullOrWhiteSpace(candidateJson)) return;

        lock (_candidateLock)
        {
            if (!_remoteDescriptionApplied)
            {
                // Trickled server candidate arrived before NegotiateAsync applied
                // setRemoteDescription(answer); buffer and flush after.
                _pendingRemoteCandidates.Add(candidateJson);
                return;
            }
        }

        ApplyRemoteCandidateInternal(candidateJson);
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
        get { lock (_candidateLock) return _pendingRemoteCandidates.Count; }
    }

    private void OnPcStateChanged(RTCPeerConnectionState state)
    {
        ConnectionStateChanged?.Invoke(state);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
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
