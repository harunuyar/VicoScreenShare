using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;

namespace ScreenSharing.Client.Services;

/// <summary>
/// WebSocket signaling client. Connects to the SFU server, exchanges envelopes, and
/// raises strongly-typed events for UI-layer subscribers. Events fire on whatever
/// thread the reader loop is on; subscribers must marshal to the UI thread themselves
/// (Avalonia's <c>Dispatcher.UIThread</c>).
///
/// Each <see cref="ConnectAsync"/> call creates a fresh <see cref="Session"/> holding
/// that connection's socket, outbound channel, and a per-session delivery lock. The
/// reader and writer tasks only touch their owning Session. Event delivery is gated
/// by the Session's delivery lock: every reader-side fire snapshots the delegate list
/// under the lock while checking <see cref="Session.IsRetired"/>, and retirement
/// acquires the same lock to flip the flag. If retirement acquires first, any later
/// reader fire sees <c>IsRetired == true</c> and stays silent, so a stale reader
/// unwinding concurrently with a new reconnect cannot leak events into the new
/// session's subscribers.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 64 * 1024;

    private readonly object _stateLock = new();
    private Session? _current;

    public event Action<string>? RoomCreated;
    public event Action<RoomJoined>? RoomJoined;
    public event Action<PeerInfo>? PeerJoined;
    public event Action<Guid, Guid?>? PeerLeft;
    public event Action<ErrorCode, string>? ServerError;
    public event Action<string?>? ConnectionLost;

    public bool IsConnected
    {
        get
        {
            var current = _current;
            return current is { IsRetired: false } && current.Socket.State == WebSocketState.Open;
        }
    }

    public async Task ConnectAsync(Uri serverUri, ClientHello hello, CancellationToken ct = default)
    {
        Session? previous;
        lock (_stateLock)
        {
            previous = _current;
            if (previous is { IsRetired: false } && previous.Socket.State == WebSocketState.Open)
            {
                throw new InvalidOperationException("SignalingClient is already connected.");
            }
        }

        // Retire any prior session before publishing a new one. This both flips
        // IsRetired under the delivery lock (suppressing any pending reader fire)
        // and awaits the reader/writer tasks so a stale reader cannot race the new
        // session's setup.
        if (previous is not null)
        {
            await RetireSessionAsync(previous).ConfigureAwait(false);
        }

        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(serverUri, ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = CreateOutboundChannel();
        var session = new Session(socket, cts, channel);

        lock (_stateLock)
        {
            _current = session;
        }

        session.ReaderTask = Task.Run(() => RunReaderAsync(session));
        session.WriterTask = Task.Run(() => RunWriterAsync(session));

        try
        {
            await SendAsync(MessageType.ClientHello, hello, ct).ConfigureAwait(false);
        }
        catch
        {
            // Partial-start rollback: retire this new session and rethrow so the
            // caller sees a clean error state and can retry.
            await RetireSessionAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    public Task CreateRoomAsync(string? password, CancellationToken ct = default) =>
        SendAsync(MessageType.CreateRoom, new CreateRoom(password), ct);

    public Task JoinRoomAsync(string roomId, string? password, CancellationToken ct = default) =>
        SendAsync(MessageType.JoinRoom, new JoinRoom(roomId, password), ct);

    public async Task SendAsync<T>(string type, T payload, CancellationToken ct = default)
    {
        var current = _current
            ?? throw new InvalidOperationException("SignalingClient is not connected.");
        var element = JsonSerializer.SerializeToElement(payload, ProtocolJson.Options);
        var envelope = new MessageEnvelope(type, null, element);
        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        await current.Outbound.Writer.WriteAsync(json, ct).ConfigureAwait(false);
    }

    private async Task RunWriterAsync(Session session)
    {
        try
        {
            await foreach (var json in session.Outbound.Reader.ReadAllAsync(session.Cts.Token).ConfigureAwait(false))
            {
                if (session.Socket.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(json);
                await session.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, session.Cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* connection loss surfaces via reader */ }
    }

    private async Task RunReaderAsync(Session session)
    {
        string? lostReason = null;
        try
        {
            while (!session.Cts.Token.IsCancellationRequested && session.Socket.State == WebSocketState.Open)
            {
                var json = await ReceiveTextAsync(session.Socket, session.Cts.Token).ConfigureAwait(false);
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

                HandleEnvelope(session, envelope);
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
            // Complete THIS session's own channel so the writer exits. Per-session,
            // never touches a newer session's channel.
            session.Outbound.Writer.TryComplete();

            // Atomic one-shot ConnectionLost delivery. Under the session's delivery
            // lock we check IsRetired, mark it retired if not, and snapshot the
            // subscriber list. The invocation itself happens outside the lock.
            Action<string?>? handler = null;
            lock (session.DeliveryLock)
            {
                if (!session.IsRetired)
                {
                    session.IsRetired = true;
                    handler = ConnectionLost;
                }
            }
            handler?.Invoke(lostReason);
        }
    }

    private void HandleEnvelope(Session session, MessageEnvelope envelope)
    {
        // Snapshot handler references inside the delivery lock, bailing out if the
        // session has already been retired. This keeps stale readers from racing
        // into the new session's subscriber list for *any* event type.
        Action<string>? onRoomCreated;
        Action<RoomJoined>? onRoomJoined;
        Action<PeerInfo>? onPeerJoined;
        Action<Guid, Guid?>? onPeerLeft;
        Action<ErrorCode, string>? onServerError;

        lock (session.DeliveryLock)
        {
            if (session.IsRetired) return;
            onRoomCreated = RoomCreated;
            onRoomJoined = RoomJoined;
            onPeerJoined = PeerJoined;
            onPeerLeft = PeerLeft;
            onServerError = ServerError;
        }

        switch (envelope.Type)
        {
            case MessageType.Ping:
                var ping = envelope.Payload.Deserialize<Ping>(ProtocolJson.Options);
                if (ping is not null)
                {
                    // Pong replies are written straight to this session's channel;
                    // never routed through SendAsync (which reads _current) so a
                    // reconnect can't send them on the new session.
                    var element = JsonSerializer.SerializeToElement(new Pong(ping.Timestamp), ProtocolJson.Options);
                    var pong = new MessageEnvelope(MessageType.Pong, null, element);
                    var json = JsonSerializer.Serialize(pong, ProtocolJson.Options);
                    session.Outbound.Writer.TryWrite(json);
                }
                break;

            case MessageType.Pong:
                break;

            case MessageType.RoomCreated:
                var created = envelope.Payload.Deserialize<RoomCreated>(ProtocolJson.Options);
                if (created is not null) onRoomCreated?.Invoke(created.RoomId);
                break;

            case MessageType.RoomJoined:
                var joined = envelope.Payload.Deserialize<RoomJoined>(ProtocolJson.Options);
                if (joined is not null) onRoomJoined?.Invoke(joined);
                break;

            case MessageType.PeerJoined:
                var pj = envelope.Payload.Deserialize<PeerJoined>(ProtocolJson.Options);
                if (pj is not null) onPeerJoined?.Invoke(pj.Peer);
                break;

            case MessageType.PeerLeft:
                var pl = envelope.Payload.Deserialize<PeerLeft>(ProtocolJson.Options);
                if (pl is not null) onPeerLeft?.Invoke(pl.PeerId, pl.NewHostPeerId);
                break;

            case MessageType.Error:
                var err = envelope.Payload.Deserialize<Error>(ProtocolJson.Options);
                if (err is not null) onServerError?.Invoke(err.Code, err.Message);
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
        Session? session;
        lock (_stateLock)
        {
            session = _current;
            _current = null;
        }
        if (session is not null)
        {
            await RetireSessionAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Atomically mark the session retired (suppressing any pending reader fire),
    /// cancel its cts, close the socket, and wait for the reader/writer tasks to
    /// finish before disposing the resources. After this returns the session is
    /// inert and cannot affect the client's event stream.
    /// </summary>
    private static async Task RetireSessionAsync(Session session)
    {
        // Flip IsRetired under the delivery lock. If the reader has not yet entered
        // its event-fire block, it will see IsRetired == true and stay silent. If
        // the reader was mid-fire (already past the check but not done invoking),
        // we wait for it via ReaderTask below.
        lock (session.DeliveryLock)
        {
            session.IsRetired = true;
        }

        try { session.Cts.Cancel(); } catch (ObjectDisposedException) { }
        session.Outbound.Writer.TryComplete();

        try
        {
            if (session.Socket.State == WebSocketState.Open)
            {
                await session.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch { }

        try { await session.ReaderTask.ConfigureAwait(false); } catch { }
        try { await session.WriterTask.ConfigureAwait(false); } catch { }

        session.Socket.Dispose();
        session.Cts.Dispose();
    }

    private static Channel<string> CreateOutboundChannel() =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// Per-connection state. Captured by the reader/writer tasks so they never touch
    /// the client's fields. <see cref="DeliveryLock"/> serializes event delivery with
    /// retirement: every reader-side fire snapshots the delegate list under the lock
    /// while checking <see cref="IsRetired"/>, and retirement acquires the same lock
    /// to flip the flag. No race window, no lost events for a live session, no stale
    /// events after retirement.
    /// </summary>
    private sealed class Session
    {
        public Session(ClientWebSocket socket, CancellationTokenSource cts, Channel<string> outbound)
        {
            Socket = socket;
            Cts = cts;
            Outbound = outbound;
        }

        public ClientWebSocket Socket { get; }
        public CancellationTokenSource Cts { get; }
        public Channel<string> Outbound { get; }
        public Task ReaderTask { get; set; } = Task.CompletedTask;
        public Task WriterTask { get; set; } = Task.CompletedTask;
        public object DeliveryLock { get; } = new();
        public bool IsRetired; // guarded by DeliveryLock
    }
}
