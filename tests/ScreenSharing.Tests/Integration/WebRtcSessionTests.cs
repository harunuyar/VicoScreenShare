using System.Net.Sockets;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScreenSharing.Client.Services;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;
using ScreenSharing.Server;
using SIPSorcery.Net;

namespace ScreenSharing.Tests.Integration;

/// <summary>
/// Phase 3.2 coverage: drive the full client-side WebRtcSession against a real
/// Kestrel host running the real <see cref="ServerHost"/> setup. Proves the
/// SignalingClient + WebRtcSession + SfuSession loop completes the SDP handshake
/// end-to-end (no media yet).
/// </summary>
public sealed class WebRtcSessionTests
{
    [Fact]
    public async Task Sender_negotiation_completes_against_real_server_host()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var server = await StartServerAsync();
        var wsUri = ResolveWsUri(server);

        var signaling = new SignalingClient();
        try
        {
            var hello = new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current);
            var roomJoinedTcs = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
            signaling.RoomJoined += j => roomJoinedTcs.TrySetResult(j);

            await signaling.ConnectAsync(wsUri, hello, cts.Token);
            await signaling.CreateRoomAsync(null, cts.Token);

            await roomJoinedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

            await using var rtc = new WebRtcSession(signaling, WebRtcRole.Sender);

            // NegotiateAsync completes when the server's SdpAnswer comes back and
            // the client's setRemoteDescription accepts it. That's what Phase 3.2
            // is proving; Phase 3.4 will assert the connection actually transitions
            // to Connected.
            await rtc.NegotiateAsync(TimeSpan.FromSeconds(10));

            rtc.PeerConnection.localDescription.Should().NotBeNull();
            rtc.PeerConnection.remoteDescription.Should().NotBeNull();
        }
        finally
        {
            await signaling.DisposeAsync();
        }
    }

    private static async Task<WebApplication> StartServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        ServerHost.ConfigureServices(builder.Services);

        var app = builder.Build();
        ServerHost.ConfigureEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private static Uri ResolveWsUri(WebApplication app)
    {
        var feature = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();
        var http = feature!.Addresses.First(a => a.StartsWith("http://"));
        var asWs = http.Replace("http://", "ws://", StringComparison.Ordinal);
        return new Uri(asWs + "/ws");
    }
}
