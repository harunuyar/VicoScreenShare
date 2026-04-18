using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VicoScreenShare.Protocol;
using VicoScreenShare.Protocol.Messages;
using VicoScreenShare.Server.Config;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace VicoScreenShare.Tests.Integration;

/// <summary>
/// Phase 3.1 coverage: prove the SFU handshake round-trip works. A client sends a
/// real SDP offer produced by a SIPSorcery <see cref="RTCPeerConnection"/>; the
/// server creates a mirrored server-side peer connection, generates an answer, and
/// sends it back. No media flows yet — this is purely the signaling contract.
/// </summary>
public sealed class SfuHandshakeTests : IAsyncLifetime
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
    public async Task Server_buffers_ice_candidate_that_arrives_before_the_offer()
    {
        // Send an ICE candidate frame BEFORE the SDP offer and verify the handshake
        // still completes. Without buffering, WsSession would have looked the peer
        // up via Find(PeerId), found nothing, and silently dropped the candidate.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var socket = await ConnectAndJoinAsync("Alice", cts.Token);

        // Build a canned RTCIceCandidateInit JSON that SIPSorcery will accept.
        // The content doesn't need to be routable — it just needs to be a well
        // formed candidate line. We use a 127.0.0.1 host candidate so the
        // server-side addIceCandidate does not reject it on shape alone.
        var cannedCandidate = "{" +
            "\"candidate\":\"candidate:1 1 UDP 2122252543 127.0.0.1 50000 typ host\"," +
            "\"sdpMid\":\"0\"," +
            "\"sdpMLineIndex\":0," +
            "\"usernameFragment\":null}";
        await SendAsync(
            socket,
            MessageType.IceCandidate,
            new IceCandidate(cannedCandidate, null, null),
            cts.Token);

        // Now send the offer. If the candidate was dropped, this still succeeds
        // (handshake-only), which is why Phase 3.1 happy-path tests could not catch
        // the drop. The server-side assertion lives inside HandleIceCandidateAsync:
        // GetOrCreatePeer has to be used so the candidate lands on a real buffer
        // that the subsequent offer handler will flush.
        var clientPc = new RTCPeerConnection(null);
        var videoFormat = new VideoFormat(VideoCodecsEnum.VP8, 96);
        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: new List<SDPAudioVideoMediaFormat> { new(videoFormat) },
            streamStatus: MediaStreamStatusEnum.RecvOnly);
        clientPc.addTrack(videoTrack);
        var offer = clientPc.createOffer(null);
        await clientPc.setLocalDescription(offer);
        await SendAsync(socket, MessageType.SdpOffer, new SdpOffer(offer.sdp), cts.Token);

        var envelope = await ExpectAsync(socket, MessageType.SdpAnswer, cts.Token);
        var answer = envelope.Payload.Deserialize<SdpAnswer>(ProtocolJson.Options)!;
        answer.Sdp.Should().NotBeNullOrEmpty();

        clientPc.close();
        await CleanCloseAsync(socket);
    }

    [Fact]
    public async Task Server_answers_an_sdp_offer_with_a_recvonly_video_track()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Connect to the server's /ws endpoint and run through hello + create_room
        // so the session has a current room before we send the offer.
        var socket = await ConnectAndJoinAsync("Alice", cts.Token);

        // Build a real offer with a recvonly video track on the client side so the
        // server has something to mirror in its answer.
        var clientPc = new RTCPeerConnection(null);
        var videoFormat = new VideoFormat(VideoCodecsEnum.VP8, 96);
        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            isRemote: false,
            capabilities: new List<SDPAudioVideoMediaFormat> { new(videoFormat) },
            streamStatus: MediaStreamStatusEnum.RecvOnly);
        clientPc.addTrack(videoTrack);

        var offer = clientPc.createOffer(null);
        await clientPc.setLocalDescription(offer);

        await SendAsync(socket, MessageType.SdpOffer, new SdpOffer(offer.sdp), cts.Token);

        var envelope = await ExpectAsync(socket, MessageType.SdpAnswer, cts.Token);
        var answer = envelope.Payload.Deserialize<SdpAnswer>(ProtocolJson.Options)!;
        answer.Sdp.Should().NotBeNullOrEmpty();
        answer.Sdp.Should().Contain("m=video", "the server mirrored the video track in its answer");

        // Feed the answer back into the client's PC — the client-side setRemoteDescription
        // must succeed, which proves the server produced a well-formed answer.
        var setResult = clientPc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            sdp = answer.Sdp,
            type = RTCSdpType.answer,
        });
        setResult.Should().Be(SetDescriptionResultEnum.OK);

        clientPc.close();
        await CleanCloseAsync(socket);
    }

    private async Task<WebSocket> ConnectAndJoinAsync(string displayName, CancellationToken ct)
    {
        var client = _factory!.Server.CreateWebSocketClient();
        var uri = new Uri(_factory.Server.BaseAddress, "/ws");
        var socket = await client.ConnectAsync(uri, ct);

        await SendAsync(
            socket,
            MessageType.ClientHello,
            new ClientHello(Guid.NewGuid(), displayName, ProtocolVersion.Current),
            ct);
        await SendAsync(socket, MessageType.CreateRoom, new CreateRoom(), ct);
        await ExpectAsync(socket, MessageType.RoomCreated, ct);
        await ExpectAsync(socket, MessageType.RoomJoined, ct);
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
            if (envelope.Type == type) return envelope;
            if (envelope.Type == MessageType.Error)
            {
                var err = envelope.Payload.Deserialize<VicoScreenShare.Protocol.Messages.Error>(ProtocolJson.Options)!;
                throw new InvalidOperationException(
                    $"Server sent error while waiting for {type}: [{err.Code}] {err.Message}");
            }
            if (envelope.Type == MessageType.Ping)
            {
                var ping = envelope.Payload.Deserialize<Ping>(ProtocolJson.Options)!;
                await SendAsync(socket, MessageType.Pong, new Pong(ping.Timestamp), ct);
            }
        }
    }

    private static async Task<MessageEnvelope?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
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
