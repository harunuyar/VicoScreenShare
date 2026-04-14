using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;
using ScreenSharing.Server.Config;

namespace ScreenSharing.Tests.Integration;

/// <summary>
/// Full-stack signaling tests: spin up the real server via WebApplicationFactory and
/// drive it with two raw WebSockets. Verifies the canonical wire format between
/// client and server, which unit tests on either side alone cannot catch.
/// </summary>
public sealed class SignalingE2ETests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.Configure<RoomServerOptions>(o =>
                    {
                        o.MaxRoomCapacity = 4;
                        o.HeartbeatInterval = TimeSpan.FromMinutes(10);
                        o.HeartbeatTimeout = TimeSpan.FromMinutes(20);
                    });
                });
            });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Two_clients_can_create_and_join_a_room_and_see_each_other()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Host creates the room.
        var host = await ConnectAsync("Alice", cts.Token);
        await SendAsync(host, MessageType.CreateRoom, new CreateRoom(), cts.Token);

        var created = await ExpectAsync(host, MessageType.RoomCreated, cts.Token);
        var roomId = created.Payload.Deserialize<RoomCreated>(ProtocolJson.Options)!.RoomId;
        roomId.Should().HaveLength(6);

        var hostJoined = await ExpectAsync(host, MessageType.RoomJoined, cts.Token);
        var hostJoinPayload = hostJoined.Payload.Deserialize<RoomJoined>(ProtocolJson.Options)!;
        hostJoinPayload.Peers.Should().HaveCount(1);
        hostJoinPayload.Peers[0].IsHost.Should().BeTrue();

        // Second client joins.
        var viewer = await ConnectAsync("Bob", cts.Token);
        await SendAsync(viewer, MessageType.JoinRoom, new JoinRoom(roomId), cts.Token);

        var viewerJoined = await ExpectAsync(viewer, MessageType.RoomJoined, cts.Token);
        var viewerPayload = viewerJoined.Payload.Deserialize<RoomJoined>(ProtocolJson.Options)!;
        viewerPayload.RoomId.Should().Be(roomId);
        viewerPayload.Peers.Should().HaveCount(2);
        viewerPayload.Peers.Single(p => p.DisplayName == "Alice").IsHost.Should().BeTrue();
        viewerPayload.Peers.Single(p => p.DisplayName == "Bob").IsHost.Should().BeFalse();

        // Host observes the peer-joined broadcast.
        var broadcast = await ExpectAsync(host, MessageType.PeerJoined, cts.Token);
        var peerJoined = broadcast.Payload.Deserialize<PeerJoined>(ProtocolJson.Options)!;
        peerJoined.Peer.DisplayName.Should().Be("Bob");

        await CleanCloseAsync(host);
        await CleanCloseAsync(viewer);
    }

    [Fact]
    public async Task Joining_unknown_room_returns_RoomNotFound_error()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var viewer = await ConnectAsync("Bob", cts.Token);
        await SendAsync(viewer, MessageType.JoinRoom, new JoinRoom("ZZZZZZ"), cts.Token);

        var err = await ExpectAsync(viewer, MessageType.Error, cts.Token);
        var payload = err.Payload.Deserialize<Error>(ProtocolJson.Options)!;
        payload.Code.Should().Be(ErrorCode.RoomNotFound);

        await CleanCloseAsync(viewer);
    }

    [Fact]
    public async Task Host_disconnect_promotes_next_peer_and_broadcasts_new_host()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var host = await ConnectAsync("Alice", cts.Token);
        await SendAsync(host, MessageType.CreateRoom, new CreateRoom(), cts.Token);
        var roomId = (await ExpectAsync(host, MessageType.RoomCreated, cts.Token))
            .Payload.Deserialize<RoomCreated>(ProtocolJson.Options)!.RoomId;
        await ExpectAsync(host, MessageType.RoomJoined, cts.Token);

        var viewer = await ConnectAsync("Bob", cts.Token);
        await SendAsync(viewer, MessageType.JoinRoom, new JoinRoom(roomId), cts.Token);
        var viewerJoin = (await ExpectAsync(viewer, MessageType.RoomJoined, cts.Token))
            .Payload.Deserialize<RoomJoined>(ProtocolJson.Options)!;
        await ExpectAsync(host, MessageType.PeerJoined, cts.Token);

        await CleanCloseAsync(host);

        var peerLeftEnvelope = await ExpectAsync(viewer, MessageType.PeerLeft, cts.Token);
        var peerLeft = peerLeftEnvelope.Payload.Deserialize<PeerLeft>(ProtocolJson.Options)!;
        peerLeft.NewHostPeerId.Should().Be(viewerJoin.YourPeerId);

        await CleanCloseAsync(viewer);
    }

    [Fact]
    public async Task Room_full_rejects_overflow_join()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Factory is configured with MaxRoomCapacity = 4.
        var host = await ConnectAsync("Host", cts.Token);
        await SendAsync(host, MessageType.CreateRoom, new CreateRoom(), cts.Token);
        var roomId = (await ExpectAsync(host, MessageType.RoomCreated, cts.Token))
            .Payload.Deserialize<RoomCreated>(ProtocolJson.Options)!.RoomId;
        await ExpectAsync(host, MessageType.RoomJoined, cts.Token);

        var extras = new List<WebSocket>();
        for (var i = 0; i < 3; i++)
        {
            var c = await ConnectAsync($"U{i}", cts.Token);
            await SendAsync(c, MessageType.JoinRoom, new JoinRoom(roomId), cts.Token);
            await ExpectAsync(c, MessageType.RoomJoined, cts.Token);
            extras.Add(c);
        }

        var overflow = await ConnectAsync("Overflow", cts.Token);
        await SendAsync(overflow, MessageType.JoinRoom, new JoinRoom(roomId), cts.Token);
        var err = await ExpectAsync(overflow, MessageType.Error, cts.Token);
        var payload = err.Payload.Deserialize<Error>(ProtocolJson.Options)!;
        payload.Code.Should().Be(ErrorCode.RoomFull);

        await CleanCloseAsync(overflow);
        foreach (var c in extras) await CleanCloseAsync(c);
        await CleanCloseAsync(host);
    }

    [Fact]
    public async Task StreamStarted_and_StreamEnded_are_broadcast_to_other_peers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var streamer = await ConnectAsync("Alice", cts.Token);
        await SendAsync(streamer, MessageType.CreateRoom, new CreateRoom(), cts.Token);
        var roomId = (await ExpectAsync(streamer, MessageType.RoomCreated, cts.Token))
            .Payload.Deserialize<RoomCreated>(ProtocolJson.Options)!.RoomId;
        var streamerJoin = (await ExpectAsync(streamer, MessageType.RoomJoined, cts.Token))
            .Payload.Deserialize<RoomJoined>(ProtocolJson.Options)!;

        var viewer = await ConnectAsync("Bob", cts.Token);
        await SendAsync(viewer, MessageType.JoinRoom, new JoinRoom(roomId), cts.Token);
        await ExpectAsync(viewer, MessageType.RoomJoined, cts.Token);
        await ExpectAsync(streamer, MessageType.PeerJoined, cts.Token);

        // Alice announces a stream with PeerId = Guid.Empty on the wire so we
        // can verify the server overrides it with her authoritative session id.
        await SendAsync(
            streamer,
            MessageType.StreamStarted,
            new StreamStarted(Guid.Empty, "stream-xyz", StreamKind.Screen, HasAudio: false),
            cts.Token);

        var started = await ExpectAsync(viewer, MessageType.StreamStarted, cts.Token);
        var startedPayload = started.Payload.Deserialize<StreamStarted>(ProtocolJson.Options)!;
        startedPayload.PeerId.Should().Be(streamerJoin.YourPeerId);
        startedPayload.StreamId.Should().Be("stream-xyz");
        startedPayload.Kind.Should().Be(StreamKind.Screen);

        // Explicit StreamEnded fans out to other peers.
        await SendAsync(
            streamer,
            MessageType.StreamEnded,
            new StreamEnded(Guid.Empty, "stream-xyz"),
            cts.Token);

        var ended = await ExpectAsync(viewer, MessageType.StreamEnded, cts.Token);
        var endedPayload = ended.Payload.Deserialize<StreamEnded>(ProtocolJson.Options)!;
        endedPayload.PeerId.Should().Be(streamerJoin.YourPeerId);
        endedPayload.StreamId.Should().Be("stream-xyz");

        await CleanCloseAsync(streamer);
        await CleanCloseAsync(viewer);
    }

    [Fact]
    public async Task Disconnecting_while_streaming_broadcasts_StreamEnded_before_PeerLeft()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var streamer = await ConnectAsync("Alice", cts.Token);
        await SendAsync(streamer, MessageType.CreateRoom, new CreateRoom(), cts.Token);
        var roomId = (await ExpectAsync(streamer, MessageType.RoomCreated, cts.Token))
            .Payload.Deserialize<RoomCreated>(ProtocolJson.Options)!.RoomId;
        var streamerJoin = (await ExpectAsync(streamer, MessageType.RoomJoined, cts.Token))
            .Payload.Deserialize<RoomJoined>(ProtocolJson.Options)!;

        var viewer = await ConnectAsync("Bob", cts.Token);
        await SendAsync(viewer, MessageType.JoinRoom, new JoinRoom(roomId), cts.Token);
        await ExpectAsync(viewer, MessageType.RoomJoined, cts.Token);
        await ExpectAsync(streamer, MessageType.PeerJoined, cts.Token);

        await SendAsync(
            streamer,
            MessageType.StreamStarted,
            new StreamStarted(Guid.Empty, "stream-crash", StreamKind.Screen, HasAudio: false),
            cts.Token);
        await ExpectAsync(viewer, MessageType.StreamStarted, cts.Token);

        // Streamer vanishes without sending StreamEnded. The server should fan
        // out a synthetic StreamEnded before the PeerLeft so the viewer can
        // clear the tile immediately.
        await CleanCloseAsync(streamer);

        var ended = await ExpectAsync(viewer, MessageType.StreamEnded, cts.Token);
        var endedPayload = ended.Payload.Deserialize<StreamEnded>(ProtocolJson.Options)!;
        endedPayload.PeerId.Should().Be(streamerJoin.YourPeerId);
        endedPayload.StreamId.Should().Be("stream-crash");

        var left = await ExpectAsync(viewer, MessageType.PeerLeft, cts.Token);
        var leftPayload = left.Payload.Deserialize<PeerLeft>(ProtocolJson.Options)!;
        leftPayload.PeerId.Should().Be(streamerJoin.YourPeerId);

        await CleanCloseAsync(viewer);
    }

    [Fact]
    public async Task Late_joiner_sees_IsStreaming_true_for_active_publisher_in_RoomJoined_snapshot()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var streamer = await ConnectAsync("Alice", cts.Token);
        await SendAsync(streamer, MessageType.CreateRoom, new CreateRoom(), cts.Token);
        var roomId = (await ExpectAsync(streamer, MessageType.RoomCreated, cts.Token))
            .Payload.Deserialize<RoomCreated>(ProtocolJson.Options)!.RoomId;
        var streamerJoin = (await ExpectAsync(streamer, MessageType.RoomJoined, cts.Token))
            .Payload.Deserialize<RoomJoined>(ProtocolJson.Options)!;

        await SendAsync(
            streamer,
            MessageType.StreamStarted,
            new StreamStarted(Guid.Empty, "live-stream", StreamKind.Screen, HasAudio: false),
            cts.Token);

        // Bob joins AFTER Alice is already streaming. His RoomJoined snapshot
        // must reflect IsStreaming=true on Alice so the client can prime its
        // clear-on-stop tracking; otherwise Alice's StreamEnded would be
        // ignored and Bob's tile would freeze on the last frame.
        var lateJoiner = await ConnectAsync("Bob", cts.Token);
        await SendAsync(lateJoiner, MessageType.JoinRoom, new JoinRoom(roomId), cts.Token);

        var joined = await ExpectAsync(lateJoiner, MessageType.RoomJoined, cts.Token);
        var payload = joined.Payload.Deserialize<RoomJoined>(ProtocolJson.Options)!;

        var alice = payload.Peers.Single(p => p.PeerId == streamerJoin.YourPeerId);
        alice.IsStreaming.Should().BeTrue(
            "Alice was streaming when Bob joined, so the snapshot should reflect it");
        var bob = payload.Peers.Single(p => p.PeerId == payload.YourPeerId);
        bob.IsStreaming.Should().BeFalse("fresh joiner has not started a stream");

        await CleanCloseAsync(streamer);
        await CleanCloseAsync(lateJoiner);
    }

    // --- helpers ---

    private async Task<WebSocket> ConnectAsync(string displayName, CancellationToken ct)
    {
        var client = _factory!.Server.CreateWebSocketClient();
        var uri = new Uri(_factory.Server.BaseAddress, "/ws");
        var socket = await client.ConnectAsync(uri, ct);
        await SendAsync(
            socket,
            MessageType.ClientHello,
            new ClientHello(Guid.NewGuid(), displayName, ProtocolVersion.Current),
            ct);
        return socket;
    }

    private static async Task SendAsync<T>(WebSocket socket, string type, T payload, CancellationToken ct)
    {
        var element = JsonSerializer.SerializeToElement(payload, ProtocolJson.Options);
        var envelope = new MessageEnvelope(type, null, element);
        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<MessageEnvelope> ExpectAsync(WebSocket socket, string type, CancellationToken ct)
    {
        while (true)
        {
            var envelope = await ReceiveAsync(socket, ct);
            if (envelope is null)
            {
                throw new InvalidOperationException($"Socket closed while waiting for {type}");
            }
            if (envelope.Type == type)
            {
                return envelope;
            }
            // Ignore unrelated messages (e.g. heartbeat pings in other tests).
            if (envelope.Type == MessageType.Ping)
            {
                var ping = envelope.Payload.Deserialize<Ping>(ProtocolJson.Options)!;
                await SendAsync(socket, MessageType.Pong, new Pong(ping.Timestamp), ct);
            }
        }
    }

    private static async Task<MessageEnvelope?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, ct);
            }
            catch (WebSocketException)
            {
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        var json = Encoding.UTF8.GetString(ms.ToArray());
        return JsonSerializer.Deserialize<MessageEnvelope>(json, ProtocolJson.Options);
    }

    private static async Task CleanCloseAsync(WebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
            }
        }
        catch { }
    }
}
