using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;
using ScreenSharing.Server.Config;
using ScreenSharing.Server.Rooms;
using ScreenSharing.Server.Sfu;

namespace ScreenSharing.Server.Signaling;

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

    public Guid PeerId { get; } = Guid.NewGuid();

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
            await LeaveCurrentRoomAsync().ConfigureAwait(false);
            _sessions.Remove(PeerId);
            await SafeCloseAsync().ConfigureAwait(false);
        }
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

            case MessageType.SdpOffer:
                await HandleSdpOfferAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.SdpAnswer:
                await HandleSdpAnswerAsync(envelope).ConfigureAwait(false);
                break;

            case MessageType.IceCandidate:
                await HandleIceCandidateAsync(envelope).ConfigureAwait(false);
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
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false)) return;
        if (_currentRoomId is not null)
        {
            await SendErrorAsync(ErrorCode.AlreadyInRoom, "Already in a room", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var request = WsMessageCodec.DecodePayload<CreateRoom>(envelope.Payload);
        var result = _rooms.CreateRoom(request.Password);
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
        var join = _rooms.TryJoin(room.Id, request.Password, peer);
        if (join.Status != JoinRoomStatus.Success)
        {
            _logger.LogWarning("Session {PeerId} could not self-join room {RoomId} after create (status {Status})", PeerId, room.Id, join.Status);
            await SendErrorAsync(ErrorCode.InternalError, "Could not self-join newly created room", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        _currentRoomId = room.Id;

        await SendAsync(MessageType.RoomCreated, new RoomCreated(room.Id), envelope.CorrelationId).ConfigureAwait(false);
        await SendRoomJoinedAsync(room, join.HostPeerId, join.SnapshotAfterJoin).ConfigureAwait(false);
    }

    private async Task HandleJoinRoomAsync(MessageEnvelope envelope)
    {
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false)) return;
        if (_currentRoomId is not null)
        {
            await SendErrorAsync(ErrorCode.AlreadyInRoom, "Already in a room", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var request = WsMessageCodec.DecodePayload<JoinRoom>(envelope.Payload);
        var peer = new RoomPeer(PeerId, _userId!.Value, _displayName);
        var result = _rooms.TryJoin(request.RoomId, request.Password, peer);

        switch (result.Status)
        {
            case JoinRoomStatus.Success:
                _currentRoomId = result.Room!.Id;
                await SendRoomJoinedAsync(result.Room, result.HostPeerId, result.SnapshotAfterJoin).ConfigureAwait(false);
                await BroadcastPeerJoinedAsync(result.Room, result.HostPeerId, peer).ConfigureAwait(false);
                break;
            case JoinRoomStatus.NotFound:
                await SendErrorAsync(ErrorCode.RoomNotFound, "Room not found", envelope.CorrelationId).ConfigureAwait(false);
                break;
            case JoinRoomStatus.InvalidPassword:
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                await SendErrorAsync(ErrorCode.InvalidPassword, "Invalid password", envelope.CorrelationId).ConfigureAwait(false);
                break;
            case JoinRoomStatus.Full:
                await SendErrorAsync(ErrorCode.RoomFull, "Room is full", envelope.CorrelationId).ConfigureAwait(false);
                break;
            case JoinRoomStatus.AlreadyIn:
                await SendErrorAsync(ErrorCode.AlreadyInRoom, "Already in this room", envelope.CorrelationId).ConfigureAwait(false);
                break;
        }
    }

    private Task SendRoomJoinedAsync(Room room, Guid hostPeerId, IReadOnlyList<RoomPeer> peers)
    {
        var peerInfos = peers
            .Select(p => new PeerInfo(p.PeerId, p.DisplayName, p.PeerId == hostPeerId, p.IsStreaming))
            .ToArray();
        var joined = new RoomJoined(
            RoomId: room.Id,
            YourPeerId: PeerId,
            Peers: peerInfos,
            IceServers: Array.Empty<IceServerConfig>());
        return SendAsync(MessageType.RoomJoined, joined);
    }

    private async Task BroadcastPeerJoinedAsync(Room room, Guid hostPeerId, RoomPeer newPeer)
    {
        var message = new PeerJoined(new PeerInfo(
            newPeer.PeerId, newPeer.DisplayName, newPeer.PeerId == hostPeerId, newPeer.IsStreaming));
        await BroadcastToRoomAsync(room, MessageType.PeerJoined, message, excludePeer: newPeer.PeerId)
            .ConfigureAwait(false);
    }

    private async Task BroadcastToRoomAsync<T>(Room room, string type, T payload, Guid? excludePeer)
    {
        foreach (var peer in room.SnapshotPeers())
        {
            if (excludePeer.HasValue && peer.PeerId == excludePeer.Value) continue;
            var session = _sessions.Get(peer.PeerId);
            if (session is null) continue;
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
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false)) return;
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
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false)) return;
        if (_currentRoomId is null)
        {
            await SendErrorAsync(ErrorCode.NotInRoom, "Must join a room before sending SDP", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var sfu = _rooms.GetSfuSession(_currentRoomId);
        var peer = sfu?.Find(PeerId);
        if (peer is null)
        {
            await SendErrorAsync(ErrorCode.BadRequest, "No pending SFU peer for this session", envelope.CorrelationId).ConfigureAwait(false);
            return;
        }

        var answer = WsMessageCodec.DecodePayload<SdpAnswer>(envelope.Payload);
        try
        {
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
        if (!await EnsureHelloAsync(envelope).ConfigureAwait(false)) return;
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

        // Trickle ICE can race the SDP offer on fast connections. Always go through
        // GetOrAttachSfuPeer so an early candidate creates the SfuPeer and is
        // buffered until HandleRemoteOfferAsync applies the remote description.
        var peer = GetOrAttachSfuPeer(sfu);
        var ice = WsMessageCodec.DecodePayload<IceCandidate>(envelope.Payload);
        peer.AddRemoteIceCandidate(ice.Candidate);
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
        if (roomId is null) return;
        _currentRoomId = null;

        var outcome = _rooms.RemovePeer(roomId, PeerId);
        if (!outcome.Found) return;

        var room = _rooms.FindRoom(roomId);
        if (room is null || outcome.PeerCountAfter == 0) return;

        var message = new PeerLeft(PeerId, outcome.WasHost ? outcome.NewHostPeerId : null);
        await BroadcastToRoomAsync(room, MessageType.PeerLeft, message, excludePeer: null)
            .ConfigureAwait(false);
    }

    private async Task<bool> EnsureHelloAsync(MessageEnvelope envelope)
    {
        if (_helloReceived) return true;
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

    private async Task RunWriterAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var json in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_socket.State != WebSocketState.Open) break;
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
