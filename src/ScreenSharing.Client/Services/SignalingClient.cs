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
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 64 * 1024;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private Task? _writerTask;
    private readonly Channel<string> _outbound = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public event Action<string>? RoomCreated;
    public event Action<RoomJoined>? RoomJoined;
    public event Action<PeerInfo>? PeerJoined;
    public event Action<Guid, Guid?>? PeerLeft;
    public event Action<ErrorCode, string>? ServerError;
    public event Action<string?>? ConnectionLost;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri serverUri, ClientHello hello, CancellationToken ct = default)
    {
        if (_socket is not null)
        {
            throw new InvalidOperationException("SignalingClient is already connected. Call DisposeAsync first.");
        }

        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(serverUri, ct).ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readerTask = Task.Run(() => RunReaderAsync(_cts.Token));
        _writerTask = Task.Run(() => RunWriterAsync(_cts.Token));

        await SendAsync(MessageType.ClientHello, hello).ConfigureAwait(false);
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
        await _outbound.Writer.WriteAsync(json, ct).ConfigureAwait(false);
    }

    private async Task RunWriterAsync(CancellationToken ct)
    {
        if (_socket is null) return;
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
        catch (Exception) { /* connection loss surfaces via reader */ }
    }

    private async Task RunReaderAsync(CancellationToken ct)
    {
        string? lostReason = null;
        try
        {
            while (!ct.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                var json = await ReceiveTextAsync(ct).ConfigureAwait(false);
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

    private async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        if (_socket is null) return null;
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
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException) { }

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
        _socket = null;
        _cts = null;
    }
}
