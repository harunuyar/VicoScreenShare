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

    private static Uri ResolveWsUri(WebApplication app)
    {
        var feature = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var http = feature!.Addresses.First(a => a.StartsWith("http://"));
        var asWs = http.Replace("http://", "ws://", StringComparison.Ordinal);
        return new Uri(asWs + "/ws");
    }
}
