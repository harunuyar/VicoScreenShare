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
/// Verifies the shared-password gate the operator configures via
/// <see cref="RoomServerOptions.AccessPassword"/>. Each test spins up its own
/// factory with a different setting so we can cover open / correct / wrong-
/// password matrices without leaking state between tests.
/// </summary>
public sealed class SignalingAuthTests
{
    private static WebApplicationFactory<Program> CreateFactory(string? accessPassword) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<RoomServerOptions>(o =>
                {
                    o.MaxRoomCapacity = 4;
                    o.HeartbeatInterval = TimeSpan.FromMinutes(10);
                    o.HeartbeatTimeout = TimeSpan.FromMinutes(20);
                    o.PeerGracePeriod = TimeSpan.FromMilliseconds(250);
                    o.AccessPassword = accessPassword;
                });
            });
        });

    [Fact]
    public async Task Open_server_accepts_hello_with_null_AccessToken()
    {
        await using var factory = CreateFactory(accessPassword: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var socket = await ConnectAsync(factory, new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current), cts.Token);

        // Server accepting a hello doesn't reply to a bare ClientHello —
        // there's no ACK on the wire. We prove acceptance by sending the
        // next message (CreateRoom) and receiving a successful reply.
        await SendAsync(socket, MessageType.CreateRoom, new CreateRoom(), cts.Token);
        var env = await ExpectAsync(socket, cts.Token);
        env.Type.Should().Be(MessageType.RoomCreated,
            "open server should accept a hello with no AccessToken and proceed to normal flow");

        await CleanCloseAsync(socket);
    }

    [Fact]
    public async Task Protected_server_accepts_hello_with_correct_AccessToken()
    {
        const string password = "correct-horse-battery-staple";
        await using var factory = CreateFactory(accessPassword: password);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var socket = await ConnectAsync(factory,
            new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current, AccessToken: password),
            cts.Token);

        await SendAsync(socket, MessageType.CreateRoom, new CreateRoom(), cts.Token);
        var env = await ExpectAsync(socket, cts.Token);
        env.Type.Should().Be(MessageType.RoomCreated);

        await CleanCloseAsync(socket);
    }

    [Fact]
    public async Task Protected_server_rejects_hello_with_wrong_AccessToken_and_closes_socket()
    {
        await using var factory = CreateFactory(accessPassword: "secret");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var socket = await ConnectAsync(factory,
            new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current, AccessToken: "WRONG"),
            cts.Token);

        // Server replies with Error(Unauthorized) then closes with PolicyViolation.
        var errorEnv = await ExpectAsync(socket, cts.Token);
        errorEnv.Type.Should().Be(MessageType.Error);
        var error = errorEnv.Payload.Deserialize<Error>(ProtocolJson.Options)!;
        error.Code.Should().Be(ErrorCode.Unauthorized);

        // Next receive should be the close frame, not another message.
        var closed = await ReceiveAsync(socket, cts.Token);
        closed.Should().BeNull("server closes the socket after the Unauthorized error");
    }

    [Fact]
    public async Task Protected_server_rejects_hello_with_null_AccessToken()
    {
        await using var factory = CreateFactory(accessPassword: "secret");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var socket = await ConnectAsync(factory,
            new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current),
            cts.Token);

        var errorEnv = await ExpectAsync(socket, cts.Token);
        errorEnv.Type.Should().Be(MessageType.Error);
        var error = errorEnv.Payload.Deserialize<Error>(ProtocolJson.Options)!;
        error.Code.Should().Be(ErrorCode.Unauthorized);
    }

    // --- helpers ---

    private static async Task<WebSocket> ConnectAsync(WebApplicationFactory<Program> factory, ClientHello hello, CancellationToken ct)
    {
        var client = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, "/ws");
        var socket = await client.ConnectAsync(uri, ct);
        await SendAsync(socket, MessageType.ClientHello, hello, ct);
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

    private static async Task<MessageEnvelope> ExpectAsync(WebSocket socket, CancellationToken ct)
    {
        var env = await ReceiveAsync(socket, ct);
        if (env is null) throw new InvalidOperationException("Socket closed unexpectedly");
        return env;
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
            if (result.EndOfMessage)
            {
                var json = Encoding.UTF8.GetString(ms.ToArray());
                return JsonSerializer.Deserialize<MessageEnvelope>(json, ProtocolJson.Options);
            }
        }
    }

    private static async Task CleanCloseAsync(WebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
        }
        catch { }
    }
}
