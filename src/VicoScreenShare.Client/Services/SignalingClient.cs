using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using VicoScreenShare.Protocol;
using VicoScreenShare.Protocol.Messages;

namespace VicoScreenShare.Client.Services;

/// <summary>
/// One-shot WebSocket signaling client. Each instance owns a single connection
/// attempt: call <see cref="ConnectAsync"/> exactly once, use the instance for the
/// lifetime of that connection, and dispose it when the operation is finished.
/// Never share or reuse an instance across operations — construct a new one for
/// the next connect. Because the event subscribers are scoped to this instance,
/// there is no way for events from a prior connection to leak into a new one.
///
/// Events fire on whatever thread the reader loop is on; subscribers must marshal
/// to the UI thread themselves (Avalonia's <c>Dispatcher.UIThread</c>).
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 64 * 1024;

    private readonly Channel<string> _outbound = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private Task? _writerTask;
    private int _state; // 0 = NotConnected, 1 = Connecting, 2 = Connected, 3 = Disposed

    public event Action<string>? RoomCreated;
    public event Action<RoomJoined>? RoomJoined;
    public event Action<PeerInfo>? PeerJoined;
    public event Action<Guid>? PeerLeft;
    public event Action<ErrorCode, string>? ServerError;
    public event Action<string?>? ConnectionLost;
    /// <summary>
    /// SDP offer from the server. The payload carries both the SDP and a nullable
    /// <see cref="SdpOffer.SubscriptionId"/>: null means the main PC (client-initiated
    /// flow), a Guid "N" string means a subscriber PC the server is driving for a
    /// specific publisher — the client must create a fresh RecvOnly PC and answer.
    /// </summary>
    public event Action<SdpOffer>? SdpOfferReceived;
    public event Action<SdpAnswer>? SdpAnswerReceived;
    public event Action<IceCandidate>? IceCandidateReceived;
    public event Action<StreamStarted>? StreamStartedReceived;
    public event Action<StreamEnded>? StreamEndedReceived;

    /// <summary>Fired when any peer in the current room transitions into or out
    /// of the server-side reconnect grace window.</summary>
    public event Action<PeerConnectionState>? PeerConnectionStateChanged;

    /// <summary>Fired when a <c>ResumeSession</c> attempt is rejected by the server.</summary>
    public event Action<ResumeFailed>? ResumeFailedReceived;

    public bool IsConnected =>
        Volatile.Read(ref _state) == StateConnected && _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri serverUri, ClientHello hello, CancellationToken ct = default)
    {
        var previous = Interlocked.CompareExchange(ref _state, StateConnecting, StateNotConnected);
        if (previous == StateDisposed)
        {
            throw new ObjectDisposedException(nameof(SignalingClient));
        }
        if (previous != StateNotConnected)
        {
            throw new InvalidOperationException(
                "SignalingClient is one-shot: create a new instance for each connect attempt.");
        }

        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(serverUri, ct).ConfigureAwait(false);
        }
        catch
        {
            // One-shot contract: a failed connect kills this instance. Transition
            // to Disposed (not back to NotConnected) so a second ConnectAsync call
            // on the same instance throws cleanly. Callers must create a new
            // SignalingClient for each attempt.
            socket.Dispose();
            Volatile.Write(ref _state, StateDisposed);
            throw;
        }

        _socket = socket;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readerTask = Task.Run(() => RunReaderAsync(_cts.Token));
        _writerTask = Task.Run(() => RunWriterAsync(_cts.Token));
        Volatile.Write(ref _state, StateConnected);

        try
        {
            await SendAsync(MessageType.ClientHello, hello, ct).ConfigureAwait(false);
        }
        catch
        {
            // Partial-start rollback: tear the socket down and rethrow so the caller
            // sees a clean failure. The caller must then create a fresh instance.
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task CreateRoomAsync(CancellationToken ct = default) =>
        SendAsync(MessageType.CreateRoom, new CreateRoom(), ct);

    public Task JoinRoomAsync(string roomId, CancellationToken ct = default) =>
        SendAsync(MessageType.JoinRoom, new JoinRoom(roomId), ct);

    /// <summary>
    /// Announce an intentional departure to the server so the peer is evicted
    /// immediately rather than sitting in the reconnect grace window.
    /// </summary>
    public Task LeaveRoomAsync(CancellationToken ct = default) =>
        SendAsync(MessageType.LeaveRoom, new LeaveRoom(), ct);

    /// <summary>
    /// Attempt to rebind to an existing room/peer slot within the server-side
    /// grace window. On failure the server replies with <c>ResumeFailed</c> and
    /// the caller must fall back to a fresh <see cref="JoinRoomAsync"/>.
    /// </summary>
    public Task ResumeSessionAsync(string roomId, string resumeToken, CancellationToken ct = default) =>
        SendAsync(MessageType.ResumeSession, new ResumeSession(roomId, resumeToken), ct);

    public Task SendSdpOfferAsync(string sdp, string? subscriptionId = null, CancellationToken ct = default) =>
        SendAsync(MessageType.SdpOffer, new SdpOffer(sdp, subscriptionId), ct);

    public Task SendSdpAnswerAsync(string sdp, string? subscriptionId = null, CancellationToken ct = default) =>
        SendAsync(MessageType.SdpAnswer, new SdpAnswer(sdp, subscriptionId), ct);

    public Task SendIceCandidateAsync(string candidateJson, string? subscriptionId = null, CancellationToken ct = default) =>
        SendAsync(MessageType.IceCandidate, new IceCandidate(candidateJson, null, null, subscriptionId), ct);

    /// <summary>
    /// Announce that this client has started a media stream. Server rewrites the
    /// PeerId to the session's authoritative id before broadcasting, so passing
    /// <see cref="Guid.Empty"/> here is fine — any value would be ignored.
    /// </summary>
    public Task SendStreamStartedAsync(string streamId, StreamKind kind, bool hasAudio, int nominalFrameRate, CancellationToken ct = default) =>
        SendAsync(MessageType.StreamStarted, new StreamStarted(Guid.Empty, streamId, kind, hasAudio, nominalFrameRate), ct);

    public Task SendStreamEndedAsync(string streamId, CancellationToken ct = default) =>
        SendAsync(MessageType.StreamEnded, new StreamEnded(Guid.Empty, streamId), ct);

    /// <summary>
    /// Opt this viewer back into a publisher's stream after a prior
    /// <see cref="SendUnsubscribeAsync"/>. Server responds by spinning up
    /// a fresh SubscriberPeer and driving an SDP offer for it.
    /// </summary>
    public Task SendSubscribeAsync(Guid publisherPeerId, CancellationToken ct = default) =>
        SendAsync(MessageType.Subscribe, new Subscribe(publisherPeerId), ct);

    /// <summary>
    /// Stop watching a publisher. Server tears down the subscriber PC so the
    /// viewer stops paying for decode bandwidth; the client's tile will
    /// unmount on the PC's close event.
    /// </summary>
    public Task SendUnsubscribeAsync(Guid publisherPeerId, CancellationToken ct = default) =>
        SendAsync(MessageType.Unsubscribe, new Unsubscribe(publisherPeerId), ct);

    public async Task SendAsync<T>(string type, T payload, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _state) != StateConnected)
        {
            throw new InvalidOperationException("SignalingClient is not connected.");
        }
        var element = JsonSerializer.SerializeToElement(payload, ProtocolJson.Options);
        var envelope = new MessageEnvelope(type, null, element);
        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        await _outbound.Writer.WriteAsync(json, ct).ConfigureAwait(false);
    }

    private async Task RunWriterAsync(CancellationToken ct)
    {
        var socket = _socket!;
        try
        {
            await foreach (var json in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* connection loss surfaces via reader */ }
    }

    private async Task RunReaderAsync(CancellationToken ct)
    {
        var socket = _socket!;
        string? lostReason = null;
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var json = await ReceiveTextAsync(socket, ct).ConfigureAwait(false);
                if (json is null)
                {
                    lostReason = "socket closed";
                    break;
                }

                MessageEnvelope envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<MessageEnvelope>(json, ProtocolJson.Options)
                        ?? throw new InvalidOperationException("null envelope");
                }
                catch (Exception)
                {
                    continue;
                }

                HandleEnvelope(envelope);
            }
        }
        catch (WebSocketException ex)
        {
            lostReason = ex.Message;
        }
        catch (OperationCanceledException)
        {
            lostReason = null;
        }
        finally
        {
            _outbound.Writer.TryComplete();
            // Fire exactly once. If Dispose is racing, it has already transitioned
            // the state to Disposed, and the check below suppresses a spurious fire.
            if (Volatile.Read(ref _state) != StateDisposed)
            {
                ConnectionLost?.Invoke(lostReason);
            }
        }
    }

    private void HandleEnvelope(MessageEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.Ping:
                var ping = envelope.Payload.Deserialize<Ping>(ProtocolJson.Options);
                if (ping is not null)
                {
                    // Pong replies go straight to the outbound channel to avoid
                    // routing through SendAsync and re-checking connection state.
                    var element = JsonSerializer.SerializeToElement(new Pong(ping.Timestamp), ProtocolJson.Options);
                    var pong = new MessageEnvelope(MessageType.Pong, null, element);
                    var json = JsonSerializer.Serialize(pong, ProtocolJson.Options);
                    _outbound.Writer.TryWrite(json);
                }
                break;

            case MessageType.Pong:
                break;

            case MessageType.RoomCreated:
                var created = envelope.Payload.Deserialize<RoomCreated>(ProtocolJson.Options);
                if (created is not null) RoomCreated?.Invoke(created.RoomId);
                break;

            case MessageType.RoomJoined:
                var joined = envelope.Payload.Deserialize<RoomJoined>(ProtocolJson.Options);
                if (joined is not null) RoomJoined?.Invoke(joined);
                break;

            case MessageType.PeerJoined:
                var pj = envelope.Payload.Deserialize<PeerJoined>(ProtocolJson.Options);
                if (pj is not null) PeerJoined?.Invoke(pj.Peer);
                break;

            case MessageType.PeerLeft:
                var pl = envelope.Payload.Deserialize<PeerLeft>(ProtocolJson.Options);
                if (pl is not null) PeerLeft?.Invoke(pl.PeerId);
                break;

            case MessageType.Error:
                var err = envelope.Payload.Deserialize<Error>(ProtocolJson.Options);
                if (err is not null) ServerError?.Invoke(err.Code, err.Message);
                break;

            case MessageType.SdpOffer:
                var offer = envelope.Payload.Deserialize<SdpOffer>(ProtocolJson.Options);
                if (offer is not null) SdpOfferReceived?.Invoke(offer);
                break;

            case MessageType.SdpAnswer:
                var answer = envelope.Payload.Deserialize<SdpAnswer>(ProtocolJson.Options);
                if (answer is not null) SdpAnswerReceived?.Invoke(answer);
                break;

            case MessageType.IceCandidate:
                var ice = envelope.Payload.Deserialize<IceCandidate>(ProtocolJson.Options);
                if (ice is not null) IceCandidateReceived?.Invoke(ice);
                break;

            case MessageType.StreamStarted:
                var started = envelope.Payload.Deserialize<StreamStarted>(ProtocolJson.Options);
                if (started is not null) StreamStartedReceived?.Invoke(started);
                break;

            case MessageType.StreamEnded:
                var ended = envelope.Payload.Deserialize<StreamEnded>(ProtocolJson.Options);
                if (ended is not null) StreamEndedReceived?.Invoke(ended);
                break;

            case MessageType.PeerConnectionState:
                var pcs = envelope.Payload.Deserialize<PeerConnectionState>(ProtocolJson.Options);
                if (pcs is not null) PeerConnectionStateChanged?.Invoke(pcs);
                break;

            case MessageType.ResumeFailed:
                var rf = envelope.Payload.Deserialize<ResumeFailed>(ProtocolJson.Options);
                if (rf is not null) ResumeFailedReceived?.Invoke(rf);
                break;
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
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
                    result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                ms.Write(buffer, 0, result.Count);
                if (ms.Length > MaxMessageBytes)
                {
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

    public async ValueTask DisposeAsync()
    {
        var prior = Interlocked.Exchange(ref _state, StateDisposed);
        if (prior == StateDisposed) return;

        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _outbound.Writer.TryComplete();

        try
        {
            if (_socket is { State: WebSocketState.Open })
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch { }

        try { if (_readerTask is not null) await _readerTask.ConfigureAwait(false); } catch { }
        try { if (_writerTask is not null) await _writerTask.ConfigureAwait(false); } catch { }

        _socket?.Dispose();
        _cts?.Dispose();
    }

    private const int StateNotConnected = 0;
    private const int StateConnecting = 1;
    private const int StateConnected = 2;
    private const int StateDisposed = 3;
}
