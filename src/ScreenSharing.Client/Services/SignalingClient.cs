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
/// that connection's socket, outbound channel, and cancellation source. The reader
/// and writer tasks only touch their owning Session, so a stale reader unwinding
/// concurrently with a new <see cref="ConnectAsync"/> cannot complete the new
/// session's channel or fire <see cref="ConnectionLost"/> into a new operation.
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

        // If there's an older session still unwinding (e.g. after a drop) or even
        // mid-finally, retire it with suppression: the caller is explicitly
        // reconnecting, so any pending ConnectionLost from the old reader must not
        // leak into the new operation's subscribers. The suppression is atomic via
        // Interlocked.CompareExchange on Session.NotifyClaimed, so the reader and
        // the retirement path can never both fire.
        if (previous is not null)
        {
            await RetireSessionAsync(previous, suppressConnectionLost: true).ConfigureAwait(false);
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
            // Partial-start rollback: retire this new session silently and rethrow so
            // the caller sees a clean error state.
            await RetireSessionAsync(session, suppressConnectionLost: true).ConfigureAwait(false);
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
            // Always complete THIS session's own channel so the writer exits. The
            // channel reference lives on the Session, so there is no chance of
            // completing a newer session's channel here.
            session.Outbound.Writer.TryComplete();

            // Claim the one-shot notification slot for this session. If retirement
            // has already claimed it (suppressing stale events during a reconnect),
            // the CAS here fails and we stay silent. Otherwise we fire exactly once.
            if (Interlocked.CompareExchange(ref session.NotifyClaimed, 1, 0) == 0)
            {
                ConnectionLost?.Invoke(lostReason);
            }
        }
    }

    private void HandleEnvelope(Session session, MessageEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.Ping:
                var ping = envelope.Payload.Deserialize<Ping>(ProtocolJson.Options);
                if (ping is not null)
                {
                    // Write directly to the session's channel — never through SendAsync,
                    // which reads _current and could race with a newer reconnect.
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
                if (pl is not null) PeerLeft?.Invoke(pl.PeerId, pl.NewHostPeerId);
                break;

            case MessageType.Error:
                var err = envelope.Payload.Deserialize<Error>(ProtocolJson.Options);
                if (err is not null) ServerError?.Invoke(err.Code, err.Message);
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
            await RetireSessionAsync(session, suppressConnectionLost: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels the session, optionally suppresses any pending ConnectionLost event
    /// from the reader, and waits for reader and writer to unwind before disposing
    /// the socket and cts. Suppression is atomic: claiming
    /// <see cref="Session.NotifyClaimed"/> before awaiting the reader guarantees that
    /// if the reader has not yet fired its finally-block event, it will lose the CAS
    /// and stay silent. If the reader has already fired, retirement is a no-op on
    /// the slot and still awaits the task to completion.
    /// </summary>
    private static async Task RetireSessionAsync(Session session, bool suppressConnectionLost)
    {
        session.IsRetired = true;

        if (suppressConnectionLost)
        {
            // Claim the notification slot atomically BEFORE the reader can run its
            // CAS in its finally. If the reader already claimed it, no harm done.
            Interlocked.Exchange(ref session.NotifyClaimed, 1);
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
    /// the client's fields. Two distinct flags:
    /// <list type="bullet">
    ///   <item><see cref="IsRetired"/> is a coarse "session has been asked to shut
    ///   down" marker, set at the start of <see cref="RetireSessionAsync"/> so
    ///   <see cref="IsConnected"/> and the "already connected" gate immediately stop
    ///   reporting the session as live.</item>
    ///   <item><see cref="NotifyClaimed"/> is a compare-exchange slot that gates the
    ///   one-shot <see cref="ConnectionLost"/> invocation: either the reader's
    ///   finally wins it, or the retirement path claims it first to suppress a
    ///   stale event, but never both.</item>
    /// </list>
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
        public volatile bool IsRetired;
        public int NotifyClaimed;
    }
}
