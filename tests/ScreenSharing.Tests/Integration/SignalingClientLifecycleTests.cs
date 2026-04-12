using System.Net.Sockets;
using System.Net.WebSockets;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScreenSharing.Client;
using ScreenSharing.Client.Services;
using ScreenSharing.Client.ViewModels;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;
using ScreenSharing.Server.Config;

namespace ScreenSharing.Tests.Integration;

/// <summary>
/// Exercises the client-side lifecycle edge cases: reusing a SignalingClient after a
/// failed ConnectAsync, and ensuring HomeViewModel recovers gracefully when the
/// socket dies mid-request.
/// </summary>
public sealed class SignalingClientLifecycleTests : IAsyncLifetime
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
    public async Task ConnectAsync_does_not_poison_client_on_failure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new SignalingClient();
        var hello = new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current);

        // First attempt: bogus port, connect should fail.
        var bogus = new Uri("ws://127.0.0.1:1/ws");
        Exception? thrown = null;
        try { await client.ConnectAsync(bogus, hello, cts.Token); }
        catch (Exception ex) { thrown = ex; }
        thrown.Should().NotBeNull();

        client.IsConnected.Should().BeFalse();

        // Second attempt: client must be reusable, not stuck in "already connected".
        // We can't use the TestServer's in-memory transport with ClientWebSocket, so
        // instead spin up a tiny listener that accepts one WebSocket handshake and
        // closes.
        await using var app = StartThrowawayWsServer(out var serverUri);
        await client.ConnectAsync(serverUri, hello, cts.Token);
        client.IsConnected.Should().BeTrue();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_is_reusable_after_DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new SignalingClient();
        var hello = new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current);

        await using var app1 = StartThrowawayWsServer(out var uri1);
        await client.ConnectAsync(uri1, hello, cts.Token);
        await client.DisposeAsync();

        // After dispose, the same instance can be reused against a fresh endpoint.
        await using var app2 = StartThrowawayWsServer(out var uri2);
        await client.ConnectAsync(uri2, hello, cts.Token);
        client.IsConnected.Should().BeTrue();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Subscriber_added_during_handler_invocation_does_not_receive_in_flight_event()
    {
        // The reader snapshots the handler delegate list inside DeliveryLock and
        // invokes that snapshot outside the lock. A new subscriber added between
        // the snapshot and the invoke must NOT receive the in-flight event: the
        // snapshot is captured by value (multicast delegate is immutable).
        //
        // We force the scenario deterministically: the first handler blocks until
        // we say so, during which time we add a second handler, then release the
        // first handler and assert the second never ran.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var server = StartRoomCreatedServer(out var uri);

        var client = new SignalingClient();
        var hello = new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current);

        var firstHandlerEntered = new ManualResetEventSlim(false);
        var firstHandlerMayProceed = new ManualResetEventSlim(false);
        var firstHandlerFired = false;
        var secondHandlerFired = false;

        void FirstHandler(string roomId)
        {
            firstHandlerFired = true;
            firstHandlerEntered.Set();
            firstHandlerMayProceed.Wait(TimeSpan.FromSeconds(5));
        }
        void SecondHandler(string roomId)
        {
            secondHandlerFired = true;
        }

        client.RoomCreated += FirstHandler;
        await client.ConnectAsync(uri, hello, cts.Token);

        // Trigger the server to emit room_created; FirstHandler will be invoked
        // and will block on firstHandlerMayProceed.
        await client.CreateRoomAsync(null, cts.Token);
        firstHandlerEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "the server's room_created must reach the first handler");

        // First handler is currently parked inside its invocation. Subscribe a
        // second handler; it was NOT present when the reader took the snapshot.
        client.RoomCreated += SecondHandler;

        // Release the first handler and let the reader continue.
        firstHandlerMayProceed.Set();

        // Give the reader a tick to finish invoking and any subsequent messages to
        // flow through. We are not expecting more room_created messages, so the
        // second handler should stay idle.
        await Task.Delay(200, cts.Token);

        firstHandlerFired.Should().BeTrue();
        secondHandlerFired.Should().BeFalse(
            "the snapshot of the delegate list was taken before SecondHandler was subscribed");

        client.RoomCreated -= FirstHandler;
        client.RoomCreated -= SecondHandler;
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_during_drop_does_not_leak_stale_ConnectionLost_into_new_session()
    {
        // Scenario: server A drops the socket, then we reconnect to server B. The old
        // reader may still be unwinding when ConnectAsync(B) runs. The new session
        // (and any subscribers added after it) must not receive the old session's
        // ConnectionLost. We run the cycle in a tight loop to raise the likelihood of
        // catching a race if one exists, and count ConnectionLost events observed
        // while we are "subscribed for the new session" — that count must stay at 0.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var client = new SignalingClient();
        var hello = new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current);

        for (var i = 0; i < 10; i++)
        {
            // Server A: accepts hello, then immediately closes on the next frame.
            await using var droppingServer = StartAbortAfterHelloServer(out var droppingUri);
            await client.ConnectAsync(droppingUri, hello, cts.Token);
            try
            {
                // Trigger the server-side close by sending a second frame.
                await client.CreateRoomAsync(null, cts.Token);
            }
            catch { /* write may race the close */ }

            // Server B: stable listener.
            await using var stableServer = StartThrowawayWsServer(out var stableUri);

            // Count ConnectionLost events from the moment we start the new operation.
            var staleEvents = 0;
            void OnConnectionLostDuringNewSession(string? _)
                => Interlocked.Increment(ref staleEvents);
            client.ConnectionLost += OnConnectionLostDuringNewSession;
            try
            {
                await client.ConnectAsync(stableUri, hello, cts.Token);
                client.IsConnected.Should().BeTrue();
                // Give any stray reader tasks a chance to run their finally blocks
                // against the new subscriber list.
                await Task.Delay(50, cts.Token);
            }
            finally
            {
                client.ConnectionLost -= OnConnectionLostDuringNewSession;
            }

            staleEvents.Should().Be(
                0,
                $"iteration {i}: stale reader from previous session must not fire ConnectionLost into the new session's subscribers");

            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task HomeViewModel_recovers_when_connection_drops_mid_create()
    {
        // Stand up a server that accepts the hello and then yanks the socket when
        // create_room arrives, so the VM is waiting on a reply that never comes.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = StartAbortAfterHelloServer(out var serverUri);

        var tempDir = Path.Combine(Path.GetTempPath(), "ScreenSharing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var identity = new IdentityStore(Path.Combine(tempDir, "profile.json"));
        identity.Save(new UserProfile { UserId = Guid.NewGuid(), DisplayName = "Alice" });

        var signaling = new SignalingClient();
        var nav = new NavigationService();
        var settings = new ClientSettings { ServerUri = serverUri };

        var vm = new HomeViewModel(identity, signaling, nav, settings)
        {
            DisplayName = "Alice",
        };

        await vm.CreateRoomCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));

        vm.IsBusy.Should().BeFalse("the VM must clear its busy flag when the server drops mid-operation");
        vm.StatusMessage.Should().NotBeNullOrEmpty();
        vm.StatusMessage.Should().Contain("Disconnected", "the VM surfaces the disconnect reason");

        await signaling.DisposeAsync();
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    // --- helpers: tiny self-hosted WS listeners (TestServer's ClientWebSocket shim is
    // not a real network transport, so we spin up real Kestrel instances here) ---

    private static WebApplication StartThrowawayWsServer(out Uri serverUri)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            // Drain the client hello then just sit there.
            var buf = new byte[1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    await socket.ReceiveAsync(buf, context.RequestAborted);
                }
            }
            catch { }
        });
        app.StartAsync().GetAwaiter().GetResult();
        serverUri = ResolveWsUri(app);
        return app;
    }

    private static WebApplication StartAbortAfterHelloServer(out Uri serverUri)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buf = new byte[4096];
            var helloReceived = false;
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var res = await socket.ReceiveAsync(buf, context.RequestAborted);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    if (!helloReceived)
                    {
                        helloReceived = true;
                        continue;
                    }
                    // Second frame (create_room) triggers an abort without sending a reply.
                    await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "simulated", CancellationToken.None);
                    break;
                }
            }
            catch { }
        });
        app.StartAsync().GetAwaiter().GetResult();
        serverUri = ResolveWsUri(app);
        return app;
    }

    /// <summary>
    /// Minimal Kestrel listener that accepts the client hello and, on receiving the
    /// follow-up create_room request, replies with a canned room_created envelope.
    /// Used to drive client-side delegate-snapshot behavior deterministically without
    /// spinning up the full signaling server.
    /// </summary>
    private static WebApplication StartRoomCreatedServer(out Uri serverUri)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buf = new byte[4096];
            var framesReceived = 0;
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var res = await socket.ReceiveAsync(buf, context.RequestAborted);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    framesReceived++;
                    // Second frame is the create_room request; reply with a canned
                    // room_created envelope so the client's RoomCreated event fires.
                    if (framesReceived == 2)
                    {
                        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
                            new ScreenSharing.Protocol.Messages.RoomCreated("TEST01"),
                            ScreenSharing.Protocol.ProtocolJson.Options);
                        var envelope = new ScreenSharing.Protocol.MessageEnvelope(
                            ScreenSharing.Protocol.MessageType.RoomCreated, null, payload);
                        var json = System.Text.Json.JsonSerializer.Serialize(
                            envelope, ScreenSharing.Protocol.ProtocolJson.Options);
                        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, context.RequestAborted);
                    }
                }
            }
            catch { }
        });
        app.StartAsync().GetAwaiter().GetResult();
        serverUri = ResolveWsUri(app);
        return app;
    }

    private static Uri ResolveWsUri(WebApplication app)
    {
        var feature = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var http = feature!.Addresses.First(a => a.StartsWith("http://"));
        var asWs = http.Replace("http://", "ws://", StringComparison.Ordinal);
        return new Uri(asWs + "/ws");
    }
}
