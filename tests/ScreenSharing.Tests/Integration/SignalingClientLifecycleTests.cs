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
/// Client-side lifecycle tests. The SignalingClient is one-shot: each connect
/// attempt owns its own instance, so most "reusability" concerns simply don't
/// apply. These tests verify that contract, that subscribers added mid-invocation
/// don't see in-flight events (the immutable-snapshot property), and that
/// HomeViewModel handles disconnect and retry cleanly by building a fresh
/// SignalingClient per operation.
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
    public async Task SignalingClient_rejects_second_ConnectAsync_on_same_instance()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = StartThrowawayWsServer(out var uri);

        var client = new SignalingClient();
        var hello = new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current);
        await client.ConnectAsync(uri, hello, cts.Token);

        var secondAttempt = async () => await client.ConnectAsync(uri, hello, cts.Token);
        await secondAttempt.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*one-shot*");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task SignalingClient_rejects_ConnectAsync_after_Dispose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = StartThrowawayWsServer(out var uri);

        var client = new SignalingClient();
        await client.DisposeAsync();

        var hello = new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current);
        var attempt = async () => await client.ConnectAsync(uri, hello, cts.Token);
        await attempt.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task SignalingClient_rejects_second_ConnectAsync_after_failed_connect()
    {
        // One-shot contract: even if the first ConnectAsync threw, the same instance
        // cannot be retried. Callers must create a fresh SignalingClient.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = new SignalingClient();
        var hello = new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current);

        var bogus = new Uri("ws://127.0.0.1:1/ws");
        Exception? firstFailure = null;
        try { await client.ConnectAsync(bogus, hello, cts.Token); }
        catch (Exception ex) { firstFailure = ex; }
        firstFailure.Should().NotBeNull();

        await using var server = StartThrowawayWsServer(out var uri);
        var secondAttempt = async () => await client.ConnectAsync(uri, hello, cts.Token);
        await secondAttempt.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task SignalingClient_releases_state_after_failed_connect_so_a_fresh_instance_works()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hello = new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current);

        // First instance: point at a bogus port, expect an exception.
        var failing = new SignalingClient();
        var bogus = new Uri("ws://127.0.0.1:1/ws");
        Exception? thrown = null;
        try { await failing.ConnectAsync(bogus, hello, cts.Token); }
        catch (Exception ex) { thrown = ex; }
        thrown.Should().NotBeNull();
        await failing.DisposeAsync();

        // Second instance (the real path a caller should take): connects cleanly.
        await using var server = StartThrowawayWsServer(out var uri);
        var fresh = new SignalingClient();
        await fresh.ConnectAsync(uri, hello, cts.Token);
        fresh.IsConnected.Should().BeTrue();
        await fresh.DisposeAsync();
    }

    [Fact]
    public async Task Subscriber_added_during_handler_invocation_does_not_receive_in_flight_event()
    {
        // Multicast delegates are immutable values, so a fire that has already
        // captured the current subscriber list will not pick up a new subscriber
        // added mid-invocation. This test parks the first handler inside its
        // invocation, subscribes a second handler while it is parked, then releases
        // the first and asserts the second never ran.
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

        await client.CreateRoomAsync(null, cts.Token);
        firstHandlerEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "the server's room_created must reach the first handler");

        client.RoomCreated += SecondHandler;
        firstHandlerMayProceed.Set();
        await Task.Delay(200, cts.Token);

        firstHandlerFired.Should().BeTrue();
        secondHandlerFired.Should().BeFalse(
            "the in-flight invocation captured the delegate list before SecondHandler was added");

        client.RoomCreated -= FirstHandler;
        client.RoomCreated -= SecondHandler;
        await client.DisposeAsync();
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

        var nav = new NavigationService();
        var settings = new ClientSettings { ServerUri = serverUri };
        var settingsStore = new SettingsStore(Path.Combine(tempDir, "settings.json"));

        var vm = new HomeViewModel(identity, () => new SignalingClient(), nav, settings, settingsStore)
        {
            DisplayName = "Alice",
        };

        await vm.CreateRoomCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));

        vm.IsBusy.Should().BeFalse("the VM must clear its busy flag when the server drops mid-operation");
        vm.StatusMessage.Should().NotBeNullOrEmpty();
        vm.StatusMessage.Should().Contain("Disconnected", "the VM surfaces the disconnect reason");

        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task HomeViewModel_can_retry_after_failed_create_and_navigates_on_success()
    {
        // Attempt 1: a server that drops mid-create, so the VM surfaces a
        // "Disconnected" status and stays on HomeViewModel.
        // Attempt 2: a server that replies with room_created + room_joined, so
        // the VM completes successfully and navigates to RoomViewModel.
        // The factory is counted to prove each attempt built a fresh client.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var droppingServer = StartAbortAfterHelloServer(out var droppingUri);
        await using var happyServer = StartRoomJoinedServer(out var happyUri);

        var tempDir = Path.Combine(Path.GetTempPath(), "ScreenSharing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var identity = new IdentityStore(Path.Combine(tempDir, "profile.json"));
        identity.Save(new UserProfile { UserId = Guid.NewGuid(), DisplayName = "Alice" });

        var nav = new NavigationService();
        var settings = new ClientSettings { ServerUri = droppingUri };
        var settingsStore = new SettingsStore(Path.Combine(tempDir, "settings.json"));

        var factoryCallCount = 0;
        SignalingClient Factory()
        {
            Interlocked.Increment(ref factoryCallCount);
            return new SignalingClient();
        }

        var vm = new HomeViewModel(identity, Factory, nav, settings, settingsStore)
        {
            DisplayName = "Alice",
        };

        // Attempt 1.
        await vm.CreateRoomCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));
        vm.IsBusy.Should().BeFalse();
        vm.StatusMessage.Should().NotBeNull();
        vm.StatusMessage!.Should().Contain("Disconnected");
        nav.CurrentViewModel.Should().BeNull(
            "the first attempt failed so we should still be on the home screen");
        factoryCallCount.Should().Be(1);

        // Attempt 2.
        settings.ServerUri = happyUri;
        await vm.CreateRoomCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));

        factoryCallCount.Should().Be(2,
            "each attempt must build a brand-new SignalingClient via the factory");
        vm.IsBusy.Should().BeFalse();

        var roomVm = nav.CurrentViewModel.Should().BeOfType<RoomViewModel>(
            "the second attempt succeeded and the VM navigated to the room view").Subject;
        roomVm.RoomId.Should().Be("TEST01");

        // Tear down the navigated RoomViewModel so the background signaling task
        // does not outlive the test.
        await roomVm.LeaveRoomCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    // --- helpers: tiny self-hosted WS listeners ---

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
    /// Kestrel listener that on receiving a create_room request replies with a
    /// canned room_created envelope followed by a room_joined envelope with a
    /// single peer (the caller, marked as host). Enough to drive the client's
    /// HomeViewModel through its full successful-join path.
    /// </summary>
    private static WebApplication StartRoomJoinedServer(out Uri serverUri)
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
            var buf = new byte[8192];
            var framesReceived = 0;
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var res = await socket.ReceiveAsync(buf, context.RequestAborted);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    framesReceived++;
                    if (framesReceived == 2)
                    {
                        const string roomId = "TEST01";
                        var peerId = Guid.NewGuid();

                        await SendEnvelopeAsync(
                            socket,
                            ScreenSharing.Protocol.MessageType.RoomCreated,
                            new ScreenSharing.Protocol.Messages.RoomCreated(roomId),
                            context.RequestAborted);

                        var joined = new ScreenSharing.Protocol.Messages.RoomJoined(
                            RoomId: roomId,
                            YourPeerId: peerId,
                            Peers: new[]
                            {
                                new ScreenSharing.Protocol.PeerInfo(peerId, "Alice", IsHost: true, IsStreaming: false),
                            },
                            IceServers: Array.Empty<ScreenSharing.Protocol.Messages.IceServerConfig>());
                        await SendEnvelopeAsync(
                            socket,
                            ScreenSharing.Protocol.MessageType.RoomJoined,
                            joined,
                            context.RequestAborted);
                    }
                }
            }
            catch { }
        });
        app.StartAsync().GetAwaiter().GetResult();
        serverUri = ResolveWsUri(app);
        return app;
    }

    private static async Task SendEnvelopeAsync<T>(
        WebSocket socket,
        string type,
        T payload,
        CancellationToken ct)
    {
        var element = System.Text.Json.JsonSerializer.SerializeToElement(
            payload, ScreenSharing.Protocol.ProtocolJson.Options);
        var envelope = new ScreenSharing.Protocol.MessageEnvelope(type, null, element);
        var json = System.Text.Json.JsonSerializer.Serialize(
            envelope, ScreenSharing.Protocol.ProtocolJson.Options);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

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
