namespace VicoScreenShare.Server.Signaling;

using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using VicoScreenShare.Protocol;
using VicoScreenShare.Protocol.Messages;
using VicoScreenShare.Server.Config;
using VicoScreenShare.Server.Rooms;
using VicoScreenShare.Server.Sfu;

/// <summary>
/// One WebSocket session. Owns a reader loop, a serialized writer loop driven by a
/// bounded channel, and a heartbeat loop that sends a ping every
/// <see cref="RoomServerOptions.HeartbeatInterval"/> and closes the socket if no pong
/// arrives within <see cref="RoomServerOptions.HeartbeatTimeout"/>.
/// </summary>
public sealed class WsSession
{
    private const int MaxMessageBytes = 64 * 1024;

    private readonly WebSocket _socket;
    private readonly RoomManager _rooms;
    private readonly SessionRegistry _sessions;
    private readonly IOptionsMonitor<RoomServerOptions> _options;
    private readonly ILogger<WsSession> _logger;

    private readonly Channel<string> _outbound = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly CancellationTokenSource _cts = new();

    private Guid? _userId;
    private string _displayName = string.Empty;
    private bool _helloReceived;
    private string? _currentRoomId;
    private long _lastPongTicks = DateTime.UtcNow.Ticks;
    private bool _sfuPeerBound;
    private string? _currentStreamId;

    private SfuSession? _currentSfuSession;
    private Func<SfuSubscriberPeer, Task>? _subscriberReadyHandler;

    public WsSession(
        WebSocket socket,
        RoomManager rooms,
        SessionRegistry sessions,
        IOptionsMonitor<RoomServerOptions> options,
        ILogger<WsSession> logger)
    {
        _socket = socket;
        _rooms = rooms;
        _sessions = sessions;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Per-session id assigned by the server. Starts as a fresh <see cref="Guid.NewGuid"/>
    /// and is rebound to the original <see cref="RoomPeer"/>'s id inside
    /// <see cref="HandleResumeSessionAsync"/> when the client successfully resumes.
    /// </summary>
    public Guid PeerId { get; private set; } = Guid.NewGuid();

    public string DisplayName => _displayName;

    public async Task RunAsync(CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _cts.Token);
        _sessions.Add(this);

        var writerTask = RunWriterAsync(linked.Token);
        var heartbeatTask = RunHeartbeatAsync(linked.Token);

        try
        {
            await RunReaderAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session {PeerId} reader ended with exception", PeerId);
        }
        finally
        {
            _cts.Cancel();
            _outbound.Writer.TryComplete();
            try { await writerTask.ConfigureAwait(false); } catch { }
            try { await heartbeatTask.ConfigureAwait(false); } catch { }
            await BeginPeerGraceOrLeaveAsync().ConfigureAwait(false);
            _sessions.Remove(PeerId);
            await SafeCloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// On WebSocket teardown, put the peer into the server-side grace window
    /// rather than immediately evicting them. During grace:
    /// <list type="bullet">
    ///   <item>The <see cref="RoomPeer"/> stays in the room with <c>IsConnected=false</c>.</item>
    ///   <item>Other peers get a <c>PeerConnectionState(IsConnected=false)</c> broadcast
    ///         so their UI can show a ghosted chip/tile.</item>
    ///   <item>If the client reconnects within the grace window via <c>ResumeSession</c>,
    ///         the <c>RoomManager.TryResume</c> path atomically cancels the expiry
    ///         timer and rebinds the new session to the same <c>PeerId</c>.</item>
    ///   <item>If the window elapses, the grace timer runs the "hard leave" path —
    ///         the same cleanup <c>LeaveCurrentRoomAsync</c> does today.</item>
    /// </list>
    /// SFU state (upstream <c>SfuPeer</c> + this viewer's subscriber PCs) is
    /// torn down on entry because SIPSorcery <c>RTCPeerConnection</c>s don't
    /// survive a WS drop. On resume the client re-offers and everything rebuilds.
    /// </summary>
    private async Task BeginPeerGraceOrLeaveAsync()
    {
        var roomId = _currentRoomId;
        if (roomId is null)
        {
            return;
        }

        StopDownstreamLossReporter();

        // Detach SFU hooks — this WsSession is about to die, it shouldn't drive
        // more subscriber offers. A resumed session re-attaches fresh.
        var sfuSession = _currentSfuSession;
        var handler = _subscriberReadyHandler;
        if (sfuSession is not null && handler is not null)
        {
            sfuSession.SubscriberReady -= handler;
        }
        _currentSfuSession = null;
        _subscriberReadyHandler = null;
        _sfuPeerBound = false;

        // Stash the streaming state on the RoomPeer so Phase 5 can re-emit
        // StreamStarted on resume without the client re-sending it.
        var room = _rooms.FindRoom(roomId);
        if (room is not null)
        {
            var peer = room.SnapshotPeers().FirstOrDefault(p => p.PeerId == PeerId);
            if (peer is not null)
            {
                peer.LastStreamId = _currentStreamId;
            }
        }

        // Evict every SFU PC tied to this peer — upstream SfuPeer (publisher
        // role) AND every (viewer=me, publisher=X) SfuSubscriberPeer. The room
        // slot stays reserved via the grace timer.
        if (sfuSession is not null)
        {
            await sfuSession.RemovePeerAsync(PeerId).ConfigureAwait(false);
        }

        // Kick off grace. The expiry callback runs the full LeaveCurrentRoomAsync
        // path on our behalf. If the client resumes within the window, the
        // callback is cancelled and never runs.
        var placedInGrace = _rooms.BeginPeerGrace(roomId, PeerId, async () =>
        {
            // Restore _currentRoomId so LeaveCurrentRoomAsync actually does work.
            _currentRoomId = roomId;
            await LeaveCurrentRoomAsync().ConfigureAwait(false);
        });

        if (placedInGrace is null)
        {
            // Room or peer already gone — nothing to broadcast.
            _currentRoomId = null;
            return;
        }

        // Broadcast disconnection so other peers ghost this chip/tile during
        // grace. Other sessions route this via SessionRegistry.Get — their
        // send path is independent of this now-dying session.
        await BroadcastToRoomAsync(
            placedInGrace,
            MessageType.PeerConnectionState,
            new PeerConnectionState(PeerId, false),
            excludePeer: PeerId).ConfigureAwait(false);

        _currentRoomId = null;
    }

    private async Task RunReaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var json = await ReceiveTextAsync(ct).ConfigureAwait(false);
            if (json is null)
            {
                return;
            }

            MessageEnvelope envelope;
            try
            {
                envelope = WsMessageCodec.DecodeEnvelope(json);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Session {PeerId} sent malformed JSON", PeerId);
                await CloseWithPolicyViolationAsync("malformed envelope").ConfigureAwait(false);
                return;
            }

            try
            {
                await DispatchAsync(envelope, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session {PeerId} dispatch failed for {Type}", PeerId, envelope.Type);
                await SendErrorAsync(ErrorCode.InternalError, "Dispatch failure", envelope.CorrelationId).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case MessageType.ClientHello:
                await HandleClientHelloAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.Ping:
                // Treat an inbound Ping as a keepalive from the client; reply Pong.
                var ping = WsMessageCodec.DecodePayload<Ping>(envelope.Payload);
                await SendAsync(MessageType.Pong, new Pong(ping.Timestamp)).ConfigureAwait(false);
                break;

            case MessageType.Pong:
                Interlocked.Exchange(ref _lastPongTicks, DateTime.UtcNow.Ticks);
                break;

            case MessageType.CreateRoom:
                await HandleCreateRoomAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.JoinRoom:
                await HandleJoinRoomAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.LeaveRoom:
                await HandleLeaveRoomAsync().ConfigureAwait(false);
                break;

            case MessageType.ResumeSession:
                await HandleResumeSessionAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.SdpOffer:
                await HandleSdpOfferAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.SdpAnswer:
                await HandleSdpAnswerAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.IceCandidate:
                await HandleIceCandidateAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.StreamStarted:
                await HandleStreamStartedAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.StreamEnded:
                await HandleStreamEndedAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.Subscribe:
                await HandleSubscribeAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.Unsubscribe:
                await HandleUnsubscribeAsync(envelope).ConfigureAwait(false);
                break;

            default:
                _logger.LogInformation("Session {PeerId} sent unknown message type {Type}", PeerId, envelope.Type);
                await SendErrorAsync(ErrorCode.BadRequest, $"Unknown message type '{envelope.Type}'", envelope.CorrelationId).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleClientHelloAsync(MessageEnvelope envelope)
    {
        if (_helloReceived)
        {
            await SendErrorAsync(ErrorCode.BadRequest, "Hello already received", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var hello = WsMessageCodec.DecodePayload<ClientHello>(envelope.Payload);
        if (hello.ProtocolVersion != ProtocolVersion.Current)
        {
            await SendErrorAsync(
                ErrorCode.ProtocolVersionMismatch,
                $"Protocol version {hello.ProtocolVersion} not supported (server expects {ProtocolVersion.Current})",
                envelope.CorrelationId).ConfigureAwait(false);
            await CloseWithPolicyViolationAsync("protocol version mismatch").ConfigureAwait(false);
            return;
        }

        // Shared-password auth. Null/empty AccessPassword on the server = open;
        // otherwise the client's AccessToken must match byte-for-byte via a
        // constant-time compare (defends against timing side channels even
        // though this is a single shared secret, not a per-user credential).
        var expectedPassword = _options.CurrentValue.AccessPassword;
        if (!string.IsNullOrEmpty(expectedPassword))
        {
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expectedPassword);
            var actualBytes = System.Text.Encoding.UTF8.GetBytes(hello.AccessToken ?? string.Empty);
            var matches = actualBytes.Length == expectedBytes.Length
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
            if (!matches)
            {
                // Send direct — the channel-based path races the close below,
                // and the client must actually see the Unauthorized envelope
                // to render a useful error message.
                await SendErrorDirectAsync(ErrorCode.Unauthorized, "Invalid access token", envelope.CorrelationId).ConfigureAwait(false);
                await CloseWithPolicyViolationAsync("unauthorized").ConfigureAwait(false);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(hello.DisplayName))
        {
            await SendErrorAsync(ErrorCode.BadRequest, "DisplayName must be non-empty", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        _userId = hello.UserId;
        _displayName = hello.DisplayName.Trim();
        _helloReceived = true;
        _logger.LogInformation("Session {PeerId} hello from {UserId} ({DisplayName})", PeerId, _userId, _displayName);
    }

    private async Task HandleCreateRoomAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false))
        {
            return;
        }

        if (_currentRoomId is not null)
        {
            await SendErrorAsync(ErrorCode.AlreadyInRoom, "Already in a room", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        // Payload is empty (CreateRoom is a marker record); decode is a
        // no-op but kept so the envelope path is symmetric with the
        // other handlers and any future fields land here without an
        // extra dispatch tweak.
        _ = WsMessageCodec.DecodePayload<CreateRoom>(envelope.Payload);
        var result = _rooms.CreateRoom();
        if (result.Status != CreateRoomStatus.Success || result.Room is null)
        {
            var (code, message) = result.Status switch
            {
                CreateRoomStatus.ServerFull => (ErrorCode.RateLimited, "Server at room capacity"),
                _ => (ErrorCode.InternalError, "Room id allocation failed"),
            };
            await SendErrorAsync(code, message, envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var room = result.Room;
        var peer = new RoomPeer(PeerId, _userId!.Value, _displayName);
        var join = _rooms.TryJoin(room.Id, peer);
        if (join.Status != JoinRoomStatus.Success)
        {
            _logger.LogWarning("Session {PeerId} could not self-join room {RoomId} after create (status {Status})", PeerId, room.Id, join.Status);
            await SendErrorAsync(ErrorCode.InternalError, "Could not self-join newly created room", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        _currentRoomId = room.Id;

        await SendAsync(MessageType.RoomCreated, new RoomCreated(room.Id), envelope.CorrelationId).ConfigureAwait(false);
        await SendRoomJoinedAsync(room, join.SnapshotAfterJoin, peer.ResumeToken ?? string.Empty).ConfigureAwait(false);

        // Attach the subscriber-offer pump so the server can drive SdpOffers
        // for this viewer whenever anyone in the room starts publishing. The
        // creator is the first peer, so OnViewerJoinedAsync is a no-op.
        var sfu = _rooms.GetSfuSession(room.Id);
        if (sfu is not null)
        {
            AttachSfuSessionHooks(sfu);
            await sfu.OnViewerJoinedAsync(PeerId).ConfigureAwait(false);
        }
    }

    private async Task HandleResumeSessionAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false))
        {
            return;
        }

        if (_currentRoomId is not null)
        {
            await SendErrorAsync(ErrorCode.AlreadyInRoom, "Already in a room", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var request = WsMessageCodec.DecodePayload<ResumeSession>(envelope.Payload);
        var outcome = _rooms.TryResume(request.RoomId, request.ResumeToken);

        switch (outcome.Status)
        {
            case ResumeOutcomeStatus.Success:
                var room = outcome.Room!;
                var peer = outcome.Peer!;

                // Rebind this session's PeerId to the original peer's. Any future
                // broadcasts (including PeerConnectionState below) and SFU routing
                // needs to find us under the SAME id that remote peers already know.
                var oldPeerId = PeerId;
                _sessions.Remove(oldPeerId);
                PeerId = peer.PeerId;
                _sessions.Add(this);

                _currentRoomId = room.Id;
                if (peer.IsStreaming && peer.LastStreamId is not null)
                {
                    _currentStreamId = peer.LastStreamId;
                }

                // Send a new RoomJoined (with a fresh roster snapshot + rotated
                // token) so the client can re-seed all its state from the
                // authoritative server view.
                await SendRoomJoinedAsync(room, room.SnapshotPeers(), outcome.NewResumeToken ?? string.Empty)
                    .ConfigureAwait(false);

                // Announce to the other peers that we're back.
                await BroadcastToRoomAsync(
                    room,
                    MessageType.PeerConnectionState,
                    new PeerConnectionState(PeerId, true),
                    excludePeer: PeerId).ConfigureAwait(false);

                // Re-wire SFU hooks and re-subscribe to live publishers.
                var sfu = _rooms.GetSfuSession(_currentRoomId);
                if (sfu is not null)
                {
                    AttachSfuSessionHooks(sfu);
                    await sfu.OnViewerJoinedAsync(PeerId).ConfigureAwait(false);
                }

                _logger.LogInformation("Session {PeerId} resumed in room {RoomId}", PeerId, _currentRoomId);
                break;

            case ResumeOutcomeStatus.TokenUnknown:
                await SendAsync(MessageType.ResumeFailed, new ResumeFailed(ResumeFailedReason.TokenUnknown), envelope.CorrelationId).ConfigureAwait(false);
                break;
            case ResumeOutcomeStatus.Expired:
                await SendAsync(MessageType.ResumeFailed, new ResumeFailed(ResumeFailedReason.Expired), envelope.CorrelationId).ConfigureAwait(false);
                break;
            case ResumeOutcomeStatus.RoomGone:
                await SendAsync(MessageType.ResumeFailed, new ResumeFailed(ResumeFailedReason.RoomGone), envelope.CorrelationId).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleLeaveRoomAsync()
    {
        // Explicit leave — evict immediately rather than sitting in grace. The
        // WS will close right after this from the client side; our finally
        // block's BeginPeerGraceOrLeaveAsync becomes a no-op because
        // _currentRoomId is already null.
        await LeaveCurrentRoomAsync().ConfigureAwait(false);
    }

    private async Task HandleJoinRoomAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false))
        {
            return;
        }

        if (_currentRoomId is not null)
        {
            await SendErrorAsync(ErrorCode.AlreadyInRoom, "Already in a room", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var request = WsMessageCodec.DecodePayload<JoinRoom>(envelope.Payload);
        var peer = new RoomPeer(PeerId, _userId!.Value, _displayName);
        var result = _rooms.TryJoin(request.RoomId, peer);

        switch (result.Status)
        {
            case JoinRoomStatus.Success:
                _currentRoomId = result.Room!.Id;
                await SendRoomJoinedAsync(result.Room, result.SnapshotAfterJoin, peer.ResumeToken ?? string.Empty).ConfigureAwait(false);
                await BroadcastPeerJoinedAsync(result.Room, peer).ConfigureAwait(false);

                // Auto-subscribe the joiner to every already-live publisher.
                var sfu = _rooms.GetSfuSession(_currentRoomId);
                if (sfu is not null)
                {
                    AttachSfuSessionHooks(sfu);
                    await sfu.OnViewerJoinedAsync(PeerId).ConfigureAwait(false);
                }
                break;
            case JoinRoomStatus.NotFound:
                await SendErrorAsync(ErrorCode.RoomNotFound, "Room not found", envelope.CorrelationId).ConfigureAwait(false);
                break;
            case JoinRoomStatus.Full:
                await SendErrorAsync(ErrorCode.RoomFull, "Room is full", envelope.CorrelationId).ConfigureAwait(false);
                break;
            case JoinRoomStatus.AlreadyIn:
                await SendErrorAsync(ErrorCode.AlreadyInRoom, "Already in this room", envelope.CorrelationId).ConfigureAwait(false);
                break;
        }
    }

    private Task SendRoomJoinedAsync(Room room, IReadOnlyList<RoomPeer> peers, string resumeToken)
    {
        var peerInfos = peers
            .Select(p => new PeerInfo(p.PeerId, p.DisplayName, p.IsStreaming, p.IsConnected))
            .ToArray();
        var joined = new RoomJoined(
            RoomId: room.Id,
            YourPeerId: PeerId,
            Peers: peerInfos,
            IceServers: Array.Empty<IceServerConfig>(),
            ResumeToken: resumeToken,
            ResumeTtl: _options.CurrentValue.PeerGracePeriod);
        return SendAsync(MessageType.RoomJoined, joined);
    }

    private async Task BroadcastPeerJoinedAsync(Room room, RoomPeer newPeer)
    {
        var message = new PeerJoined(new PeerInfo(
            newPeer.PeerId, newPeer.DisplayName, newPeer.IsStreaming, newPeer.IsConnected));
        await BroadcastToRoomAsync(room, MessageType.PeerJoined, message, excludePeer: newPeer.PeerId)
            .ConfigureAwait(false);
    }

    private async Task BroadcastToRoomAsync<T>(Room room, string type, T payload, Guid? excludePeer)
    {
        foreach (var peer in room.SnapshotPeers())
        {
            if (excludePeer.HasValue && peer.PeerId == excludePeer.Value)
            {
                continue;
            }

            var session = _sessions.Get(peer.PeerId);
            if (session is null)
            {
                continue;
            }

            try
            {
                await session.SendAsync(type, payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to forward {Type} to peer {PeerId}", type, peer.PeerId);
            }
        }
    }

    private async Task HandleSdpOfferAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false))
        {
            return;
        }

        if (_currentRoomId is null)
        {
            await SendErrorAsync(ErrorCode.NotInRoom, "Must join a room before sending SDP", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var sfu = _rooms.GetSfuSession(_currentRoomId);
        if (sfu is null)
        {
            await SendErrorAsync(ErrorCode.InternalError, "No SFU session for room", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var offer = WsMessageCodec.DecodePayload<SdpOffer>(envelope.Payload);

        // Client-initiated offers only ever target the main SfuPeer. Subscriber
        // PCs are server-initiated, so a client SdpOffer with a non-null
        // SubscriptionId is a protocol violation.
        if (!string.IsNullOrEmpty(offer.SubscriptionId))
        {
            await SendErrorAsync(ErrorCode.BadRequest, "SubscriptionId is server-driven; clients must not offer on a subscriber PC", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var peer = GetOrAttachSfuPeer(sfu);

        try
        {
            var answerSdp = await peer.HandleRemoteOfferAsync(offer.Sdp).ConfigureAwait(false);
            await SendAsync(MessageType.SdpAnswer, new SdpAnswer(answerSdp), envelope.CorrelationId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {PeerId} SDP offer handling failed", PeerId);
            await SendErrorAsync(ErrorCode.InternalError, $"SDP offer handling failed: {ex.Message}", envelope.CorrelationId).ConfigureAwait(false);
        }
    }

    private async Task HandleSdpAnswerAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false))
        {
            return;
        }

        if (_currentRoomId is null)
        {
            await SendErrorAsync(ErrorCode.NotInRoom, "Must join a room before sending SDP", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var sfu = _rooms.GetSfuSession(_currentRoomId);
        if (sfu is null)
        {
            await SendErrorAsync(ErrorCode.InternalError, "No SFU session for room", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var answer = WsMessageCodec.DecodePayload<SdpAnswer>(envelope.Payload);

        try
        {
            // Answer for a subscriber PC — the server was the offerer. Route to
            // the matching SfuSubscriberPeer by (this viewer, publisher id).
            if (TryParseSubscriptionPublisher(answer.SubscriptionId, out var publisherId))
            {
                var sub = sfu.FindSubscriber(PeerId, publisherId);
                if (sub is null)
                {
                    _logger.LogDebug("Session {PeerId} answer for unknown subscription {Sub}", PeerId, answer.SubscriptionId);
                    return;
                }
                sub.HandleRemoteAnswer(answer.Sdp);
                return;
            }

            // Answer for the main PC — shouldn't happen in the current flow
            // (client offers, server answers) but kept for symmetry.
            var peer = sfu.Find(PeerId);
            if (peer is null)
            {
                await SendErrorAsync(ErrorCode.BadRequest, "No pending SFU peer for this session", envelope.CorrelationId).ConfigureAwait(false);
                return;
            }
            peer.HandleRemoteAnswer(answer.Sdp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {PeerId} SDP answer handling failed", PeerId);
            await SendErrorAsync(ErrorCode.InternalError, $"SDP answer handling failed: {ex.Message}", envelope.CorrelationId).ConfigureAwait(false);
        }
    }

    private async Task HandleIceCandidateAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false))
        {
            return;
        }

        if (_currentRoomId is null)
        {
            await SendErrorAsync(ErrorCode.NotInRoom, "Must join a room before sending ICE", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var sfu = _rooms.GetSfuSession(_currentRoomId);
        if (sfu is null)
        {
            await SendErrorAsync(ErrorCode.InternalError, "No SFU session for room", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var ice = WsMessageCodec.DecodePayload<IceCandidate>(envelope.Payload);

        if (TryParseSubscriptionPublisher(ice.SubscriptionId, out var publisherId))
        {
            var sub = sfu.FindSubscriber(PeerId, publisherId);
            if (sub is null)
            {
                // Candidate can race ahead of the subscription being created on
                // this side (e.g. viewer answered fast, server hasn't finished
                // wire-up). Drop quietly — SIPSorcery would drop it too if the
                // remote description isn't applied yet.
                _logger.LogDebug("Session {PeerId} ICE for unknown subscription {Sub}", PeerId, ice.SubscriptionId);
                return;
            }
            sub.AddRemoteIceCandidate(ice.Candidate);
            return;
        }

        // Trickle ICE for the main PC. Go through GetOrAttachSfuPeer so an early
        // candidate creates the SfuPeer and is buffered until the offer applies
        // the remote description.
        var peer = GetOrAttachSfuPeer(sfu);
        peer.AddRemoteIceCandidate(ice.Candidate);
    }

    private static bool TryParseSubscriptionPublisher(string? subscriptionId, out Guid publisherId)
    {
        if (!string.IsNullOrEmpty(subscriptionId) && Guid.TryParseExact(subscriptionId, "N", out publisherId))
        {
            return true;
        }
        publisherId = Guid.Empty;
        return false;
    }

    private async Task HandleStreamStartedAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false))
        {
            return;
        }

        if (_currentRoomId is null)
        {
            await SendErrorAsync(ErrorCode.NotInRoom, "Must join a room before announcing a stream", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var room = _rooms.FindRoom(_currentRoomId);
        if (room is null)
        {
            return;
        }

        var request = WsMessageCodec.DecodePayload<StreamStarted>(envelope.Payload);

        // Server is authoritative on the PeerId the message carries — a client
        // cannot claim to be someone else.
        if (!room.TrySetPeerStreaming(PeerId, true))
        {
            // Either already marked streaming or peer has been removed; either
            // way, no fan-out needed.
            return;
        }
        _currentStreamId = request.StreamId;

        var authoritative = request with { PeerId = PeerId };
        await BroadcastToRoomAsync(room, MessageType.StreamStarted, authoritative, excludePeer: PeerId)
            .ConfigureAwait(false);

        // Spin up per-viewer subscriber PCs so the server can fan out this
        // peer's RTP on dedicated outbound tracks/SSRCs to each viewer.
        var sfu = _rooms.GetSfuSession(_currentRoomId);
        if (sfu is not null)
        {
            AttachSfuSessionHooks(sfu);
            await sfu.OnPublisherStartedAsync(PeerId).ConfigureAwait(false);
            StartDownstreamLossReporter(sfu);
        }
    }

    private CancellationTokenSource? _downstreamLossCts;

    /// <summary>
    /// While this peer is publishing, poll the SFU's aggregated "worst
    /// subscriber fraction-lost" and push a <see cref="Protocol.Messages.DownstreamLossReport"/>
    /// back to the publisher every second. The publisher's adaptive-bitrate
    /// controller consumes this as a second loss signal (alongside the
    /// upstream RR it already sees), so congestion on the server→subscriber
    /// hop reduces the encoder's target bitrate even though the publisher's
    /// own upstream link is clean.
    /// </summary>
    private void StartDownstreamLossReporter(SfuSession sfu)
    {
        StopDownstreamLossReporter();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _downstreamLossCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
                    var worst = sfu.GetWorstDownstreamFractionLost(PeerId);
                    var fraction = worst / 256.0;
                    try
                    {
                        await SendAsync(MessageType.DownstreamLossReport,
                            new DownstreamLossReport(PeerId, fraction))
                            .ConfigureAwait(false);
                    }
                    catch { /* socket dying */ }
                }
            }
            catch (OperationCanceledException) { /* expected on teardown */ }
        }, cts.Token);
    }

    private void StopDownstreamLossReporter()
    {
        var cts = _downstreamLossCts;
        _downstreamLossCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    private async Task HandleStreamEndedAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false))
        {
            return;
        }

        if (_currentRoomId is null)
        {
            return;
        }

        var room = _rooms.FindRoom(_currentRoomId);
        if (room is null)
        {
            return;
        }

        if (!room.TrySetPeerStreaming(PeerId, false))
        {
            return;
        }
        var streamId = _currentStreamId ?? string.Empty;
        _currentStreamId = null;

        var authoritative = new StreamEnded(PeerId, streamId);
        await BroadcastToRoomAsync(room, MessageType.StreamEnded, authoritative, excludePeer: PeerId)
            .ConfigureAwait(false);

        var sfu = _rooms.GetSfuSession(_currentRoomId);
        if (sfu is not null)
        {
            await sfu.OnPublisherStoppedAsync(PeerId).ConfigureAwait(false);
        }
        StopDownstreamLossReporter();
    }

    private async Task HandleSubscribeAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false))
        {
            return;
        }

        if (_currentRoomId is null)
        {
            await SendErrorAsync(ErrorCode.NotInRoom, "Must join a room first", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var sfu = _rooms.GetSfuSession(_currentRoomId);
        if (sfu is null)
        {
            return;
        }

        var request = WsMessageCodec.DecodePayload<Subscribe>(envelope.Payload);

        // Ignore silently if the publisher isn't actively streaming — the
        // client may have raced a StreamEnded and it's easier to noop than
        // to bounce an error.
        if (!sfu.IsPublishing(request.PublisherPeerId))
        {
            return;
        }

        AttachSfuSessionHooks(sfu);
        await sfu.SubscribeAsync(PeerId, request.PublisherPeerId).ConfigureAwait(false);
    }

    private async Task HandleUnsubscribeAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false))
        {
            return;
        }

        if (_currentRoomId is null)
        {
            await SendErrorAsync(ErrorCode.NotInRoom, "Must join a room first", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var sfu = _rooms.GetSfuSession(_currentRoomId);
        if (sfu is null)
        {
            return;
        }

        var request = WsMessageCodec.DecodePayload<Unsubscribe>(envelope.Payload);
        await sfu.UnsubscribeAsync(PeerId, request.PublisherPeerId).ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribe to <see cref="SfuSession.SubscriberReady"/> exactly once. When a
    /// subscriber PC is created for this viewer, we create its server-side SDP
    /// offer and ship it through the signaling channel with SubscriptionId =
    /// publisher PeerId.
    /// </summary>
    private void AttachSfuSessionHooks(SfuSession sfu)
    {
        if (_currentSfuSession is not null)
        {
            return;
        }

        _currentSfuSession = sfu;
        _subscriberReadyHandler = OnSubscriberReadyAsync;
        sfu.SubscriberReady += _subscriberReadyHandler;
    }

    /// <summary>
    /// Ship a <see cref="RequestKeyframe"/> to this session's peer, asking their
    /// publisher-side <c>CaptureStreamer</c> to flush an IDR on the next frame.
    /// Called by another session when a new subscriber for this publisher's
    /// stream finishes ICE+DTLS setup — without this the late joiner waits up
    /// to one GOP (default 2 s, configurable up to 10 s) for the next natural
    /// keyframe, which is the dominant "takes ages to see video" symptom.
    /// </summary>
    public Task SendRequestKeyframeAsync()
    {
        var streamId = _currentStreamId;
        if (string.IsNullOrEmpty(streamId))
        {
            return Task.CompletedTask;
        }
        return SendAsync(MessageType.RequestKeyframe, new RequestKeyframe(streamId));
    }

    private async Task OnSubscriberReadyAsync(SfuSubscriberPeer sub)
    {
        // Only drive offers to the viewer this subscriber is paired with.
        if (sub.ViewerPeerId != PeerId)
        {
            return;
        }

        // When this subscriber's PC finishes ICE+DTLS, ask the publisher for a
        // fresh keyframe so this viewer doesn't have to wait for the next
        // natural GOP boundary (up to 2 s by default).
        var publisherPeerId = sub.PublisherPeerId;
        sub.Connected += () =>
        {
            var publisherSession = _sessions.Get(publisherPeerId);
            if (publisherSession is null)
            {
                return;
            }
            try
            {
                _ = publisherSession.SendRequestKeyframeAsync();
            }
            catch { /* publisher tearing down */ }
        };

        // ICE candidates must NOT reach the client before the SdpOffer that
        // creates the matching SubscriberSession — otherwise they're dispatched
        // to an event with no subscribers and silently dropped, and ICE on the
        // client side starves. SIPSorcery starts gathering inside
        // CreateOfferAsync's setLocalDescription, so candidates fire before the
        // offer is queued on the wire. Buffer them locally until the offer has
        // been sent, then flush in order.
        var subscriptionId = sub.SubscriptionId;
        var pendingCandidates = new List<string>();
        var candidateLock = new object();
        var offerSent = false;

        sub.LocalIceCandidateReady += candidateJson =>
        {
            lock (candidateLock)
            {
                if (!offerSent)
                {
                    pendingCandidates.Add(candidateJson);
                    return;
                }
            }

            try
            {
                _ = SendAsync(MessageType.IceCandidate,
                    new IceCandidate(candidateJson, null, null, subscriptionId));
            }
            catch { /* session tearing down */ }
        };

        try
        {
            var offerSdp = await sub.CreateOfferAsync().ConfigureAwait(false);
            await SendAsync(MessageType.SdpOffer, new SdpOffer(offerSdp, sub.SubscriptionId))
                .ConfigureAwait(false);

            List<string> toFlush;
            lock (candidateLock)
            {
                offerSent = true;
                toFlush = new List<string>(pendingCandidates);
                pendingCandidates.Clear();
            }
            foreach (var candidateJson in toFlush)
            {
                await SendAsync(MessageType.IceCandidate,
                    new IceCandidate(candidateJson, null, null, subscriptionId))
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session {PeerId} failed to drive subscriber offer for {Publisher}",
                PeerId, sub.PublisherPeerId);
        }
    }

    private SfuPeer GetOrAttachSfuPeer(SfuSession sfu)
    {
        var peer = sfu.GetOrCreatePeer(PeerId);
        // Subscribe to the server-side peer's local ICE candidates so we can forward
        // them to the client. Only subscribe once — SfuPeer.GetOrCreatePeer returns
        // the same instance on subsequent calls, so track a flag.
        if (!_sfuPeerBound)
        {
            _sfuPeerBound = true;
            peer.LocalIceCandidateReady += OnServerIceCandidateReady;
        }
        return peer;
    }

    private void OnServerIceCandidateReady(string candidateJson)
    {
        try
        {
            // Fire-and-forget: the write goes through our outbound channel.
            _ = SendAsync(MessageType.IceCandidate, new IceCandidate(candidateJson, null, null));
        }
        catch { /* session tearing down */ }
    }

    private async Task LeaveCurrentRoomAsync()
    {
        var roomId = _currentRoomId;
        if (roomId is null)
        {
            return;
        }

        _currentRoomId = null;

        // Detach the subscriber-offer pump so the shared SfuSession.SubscriberReady
        // event stops holding a reference to this (soon-to-be-dead) session. We
        // hold the delegate reference separately because SfuSession.SubscriberReady
        // is typed as Func&lt;SfuSubscriberPeer, Task&gt; — method-group unsubscribe
        // works too, but the explicit capture is clearer and race-free.
        var sfuSession = _currentSfuSession;
        var handler = _subscriberReadyHandler;
        if (sfuSession is not null && handler is not null)
        {
            sfuSession.SubscriberReady -= handler;
        }
        _currentSfuSession = null;
        _subscriberReadyHandler = null;

        // Capture the stream id BEFORE RemovePeer so we can still fan out a
        // StreamEnded for the crash/disconnect case — otherwise viewers would
        // keep displaying the last decoded frame until the PeerLeft message
        // arrives, and even then they would need to reconstruct the link
        // between "that peer was the streamer" and "clear my tile."
        var wasStreaming = _currentStreamId is not null;
        var endedStreamId = _currentStreamId;
        _currentStreamId = null;

        var outcome = _rooms.RemovePeer(roomId, PeerId);
        if (!outcome.Found)
        {
            return;
        }

        var room = _rooms.FindRoom(roomId);
        if (room is null || outcome.PeerCountAfter == 0)
        {
            return;
        }

        if (wasStreaming)
        {
            var streamEnded = new StreamEnded(PeerId, endedStreamId ?? string.Empty);
            await BroadcastToRoomAsync(room, MessageType.StreamEnded, streamEnded, excludePeer: null)
                .ConfigureAwait(false);
        }

        var message = new PeerLeft(PeerId);
        await BroadcastToRoomAsync(room, MessageType.PeerLeft, message, excludePeer: null)
            .ConfigureAwait(false);
    }

    private async Task<bool> EnsureHelloAsync(MessageEnvelope envelope)
    {
        if (_helloReceived)
        {
            return true;
        }

        await SendErrorAsync(ErrorCode.BadRequest, "ClientHello must be sent first", envelope.CorrelationId).ConfigureAwait(false);
        return false;
    }

    public async Task SendAsync<T>(string type, T payload, string? correlationId = null)
    {
        var json = WsMessageCodec.EncodeEnvelope(type, payload, correlationId);
        await _outbound.Writer.WriteAsync(json, _cts.Token).ConfigureAwait(false);
    }

    private async Task SendErrorAsync(ErrorCode code, string message, string? correlationId)
    {
        try
        {
            await SendAsync(MessageType.Error, new Error(code, message), correlationId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    /// <summary>
    /// Same payload as <see cref="SendErrorAsync"/> but bypasses the outbound
    /// channel to write straight to the socket. Used for terminal errors
    /// (e.g. Unauthorized) that must land on the wire BEFORE the follow-up
    /// CloseAsync — the channel-based path races the close and the client
    /// sometimes sees a bare close frame with no error envelope.
    /// </summary>
    private async Task SendErrorDirectAsync(ErrorCode code, string message, string? correlationId)
    {
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                return;
            }

            var json = WsMessageCodec.EncodeEnvelope(MessageType.Error, new Error(code, message), correlationId);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    private async Task RunWriterAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var json in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open)
                {
                    break;
                }

                var bytes = Encoding.UTF8.GetBytes(json);
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Session {PeerId} writer socket exception", PeerId);
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        var interval = _options.CurrentValue.HeartbeatInterval;
        var timeout = _options.CurrentValue.HeartbeatTimeout;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                try
                {
                    await SendAsync(MessageType.Ping, new Ping(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return;
                }

                var lastPong = new DateTime(Interlocked.Read(ref _lastPongTicks), DateTimeKind.Utc);
                if (DateTime.UtcNow - lastPong > timeout)
                {
                    _logger.LogInformation("Session {PeerId} heartbeat timeout; closing", PeerId);
                    _cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await CloseWithPolicyViolationAsync("binary frames not supported in Phase 1").ConfigureAwait(false);
                    return null;
                }

                ms.Write(buffer, 0, result.Count);
                if (ms.Length > MaxMessageBytes)
                {
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, "message exceeds 64KB").ConfigureAwait(false);
                    return null;
                }

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Task CloseWithPolicyViolationAsync(string reason) =>
        CloseAsync(WebSocketCloseStatus.PolicyViolation, reason);

    private async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(status, reason, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { }
    }

    private async Task SafeCloseAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { }
    }
}
