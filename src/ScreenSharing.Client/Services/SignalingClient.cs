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
/// Instances are reusable: after a failed <see cref="ConnectAsync"/> or a lost
/// connection the caller may call <see cref="ConnectAsync"/> again. The outbound
/// channel and internal task handles are recreated per connect so a completed channel
/// from a prior session never poisons the next attempt.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 64 * 1024;

    private readonly object _stateLock = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private Task? _writerTask;
    private Channel<string> _outbound = CreateOutboundChannel();

    public event Action<string>? RoomCreated;
    public event Action<RoomJoined>? RoomJoined;
    public event Action<PeerInfo>? PeerJoined;
    public event Action<Guid, Guid?>? PeerLeft;
    public event Action<ErrorCode, string>? ServerError;
    public event Action<string?>? ConnectionLost;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri serverUri, ClientHello hello, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_socket is not null && _socket.State == WebSocketState.Open)
            {
                throw new InvalidOperationException("SignalingClient is already connected.");
            }
            // Reset per-connection state. A previously-completed channel or cancelled
            // cts must not bleed into the new session.
            _outbound.Writer.TryComplete();
            _outbound = CreateOutboundChannel();
            _cts?.Dispose();
            _cts = null;
            _readerTask = null;
            _writerTask = null;
        }

        // ClientWebSocket is the only resource that has to survive the connect attempt
        // if it succeeds and must be disposed if it fails. Work on a local until the
        // connect call returns, then publish to the field only on success.
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
        lock (_stateLock)
        {
            _socket = socket;
            _cts = cts;
            _readerTask = Task.Run(() => RunReaderAsync(cts.Token));
            _writerTask = Task.Run(() => RunWriterAsync(cts.Token));
        }

        try
        {
            await SendAsync(MessageType.ClientHello, hello, ct).ConfigureAwait(false);
        }
        catch
        {
            // Rolling back a partially-started session: dispose and rethrow so the
            // caller can surface a fresh error and retry cleanly.
            await DisposeInternalAsync(suppressConnectionLost: true).ConfigureAwait(false);
            throw;
        }
    }

    public Task CreateRoomAsync(string? password, CancellationToken ct = default) =>
        SendAsync(MessageType.CreateRoom, new CreateRoom(password), ct);

    public Task JoinRoomAsync(string roomId, string? password, CancellationToken ct = default) =>
        SendAsync(MessageType.JoinRoom, new JoinRoom(roomId, password), ct);

    public async Task SendAsync<T>(string type, T payload, CancellationToken ct = default)
    {
        var element = JsonSerializer.SerializeToElement(payload, ProtocolJson.Options);
        var envelope = new MessageEnvelope(type, null, element);
        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        var outbound = _outbound;
        await outbound.Writer.WriteAsync(json, ct).ConfigureAwait(false);
    }

    private async Task RunWriterAsync(CancellationToken ct)
    {
        var socket = _socket;
        var channel = _outbound;
        if (socket is null) return;
        try
        {
            await foreach (var json in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
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
        var socket = _socket;
        if (socket is null) return;

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
            ConnectionLost?.Invoke(lostReason);
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
                    _ = SendAsync(MessageType.Pong, new Pong(ping.Timestamp));
                }
                break;

            case MessageType.Pong:
                // Heartbeat reply; nothing to do on the client side in Phase 1.
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

    public ValueTask DisposeAsync() => DisposeInternalAsync(suppressConnectionLost: false);

    private async ValueTask DisposeInternalAsync(bool suppressConnectionLost)
    {
        Action<string?>? restoredHandler = null;
        if (suppressConnectionLost)
        {
            restoredHandler = ConnectionLost;
            ConnectionLost = null;
        }

        ClientWebSocket? socket;
        CancellationTokenSource? cts;
        Task? readerTask;
        Task? writerTask;
        Channel<string> channel;

        lock (_stateLock)
        {
            socket = _socket;
            cts = _cts;
            readerTask = _readerTask;
            writerTask = _writerTask;
            channel = _outbound;

            _socket = null;
            _cts = null;
            _readerTask = null;
            _writerTask = null;
        }

        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        channel.Writer.TryComplete();

        try
        {
            if (socket is { State: WebSocketState.Open })
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch { }

        try { if (readerTask is not null) await readerTask.ConfigureAwait(false); } catch { }
        try { if (writerTask is not null) await writerTask.ConfigureAwait(false); } catch { }

        socket?.Dispose();
        cts?.Dispose();

        if (restoredHandler is not null)
        {
            ConnectionLost = restoredHandler;
        }
    }

    private static Channel<string> CreateOutboundChannel() =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
}
