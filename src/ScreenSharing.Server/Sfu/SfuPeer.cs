using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ScreenSharing.Server.Sfu;

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

        // The SFU must have a track to pair with whatever direction the client's
        // offer proposes. A SendRecv VP8 track matches both a streamer's sendonly
        // offer (server answers with recvonly) and a viewer's recvonly offer
        // (server answers with sendonly). SIPSorcery requires tracks to exist
        // before setRemoteDescription, otherwise the media-type matcher returns
        // NoMatchingMediaType. Phase 3.4 will upgrade this to a proper forwarder
        // that plumbs received RTP to other peers' send tracks.
        var videoFormat = new VideoFormat(VideoCodecsEnum.VP8, 96);
        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: new List<SDPAudioVideoMediaFormat> { new(videoFormat) },
            streamStatus: MediaStreamStatusEnum.SendRecv);
        _pc.addTrack(videoTrack);

        _pc.onicecandidate += OnLocalIceCandidate;
        _pc.onconnectionstatechange += OnConnectionStateChange;
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
    /// Consume a remote offer and return the server's answer SDP.
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

    public void AddRemoteIceCandidate(string candidateJson)
    {
        if (string.IsNullOrWhiteSpace(candidateJson)) return;

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
