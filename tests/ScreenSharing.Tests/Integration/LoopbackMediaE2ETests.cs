using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Services;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;
using ScreenSharing.Server;
using SIPSorcery.Net;

namespace ScreenSharing.Tests.Integration;

/// <summary>
/// End-to-end proof: spin up a real server host, connect two clients, have one
/// publish VP8 via <see cref="CaptureStreamer"/> + <see cref="SignalingClient.SendStreamStartedAsync"/>,
/// and verify the other receives and decodes frames through the per-publisher
/// <see cref="SubscriberSession"/> the server drives.
///
/// Exercises: captured BGRA pixels → I420 → VP8 encoder → RTP → server SfuPeer
/// (recvonly) → SfuSubscriberPeer (per-viewer sendonly) → RTP → SubscriberSession
/// RecvOnly PC → VP8 decoder → BGRA.
/// </summary>
public sealed class LoopbackMediaE2ETests
{
    [Fact]
    public async Task Publisher_and_viewer_exchange_vp8_frames_through_the_sfu()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        await using var server = await StartServerAsync();
        var wsUri = ResolveWsUri(server);

        // --- Publisher side: create room, warm the main send PC, attach CaptureStreamer. ---
        var senderSignaling = new SignalingClient();
        var senderRoomJoined = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
        senderSignaling.RoomJoined += j => senderRoomJoined.TrySetResult(j);

        await senderSignaling.ConnectAsync(wsUri, new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current), cts.Token);
        await senderSignaling.CreateRoomAsync(cts.Token);
        var senderJoin = await senderRoomJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        var roomId = senderJoin.RoomId;
        var senderPeerId = senderJoin.YourPeerId;

        await using var senderRtc = new WebRtcSession(senderSignaling, WebRtcRole.Sender);
        await senderRtc.NegotiateAsync(TimeSpan.FromSeconds(15));

        var fakeSource = new FakeCaptureSource();
        using var capture = new CaptureStreamer(
            fakeSource,
            (duration, payload, _) => senderRtc.PeerConnection.SendVideo(duration, payload),
            new VideoSettings());
        capture.Start();

        // --- Viewer side: join room. Subscribe to the SdpOffer event so the
        //     server-driven subscriber flow can deliver Alice's video. ---
        var receiverSignaling = new SignalingClient();
        var receiverRoomJoined = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverSignaling.RoomJoined += j => receiverRoomJoined.TrySetResult(j);

        SubscriberSession? subscriber = null;
        var subscriberReady = new TaskCompletionSource<SubscriberSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverSignaling.SdpOfferReceived += offer =>
        {
            if (string.IsNullOrEmpty(offer.SubscriptionId)) return;
            if (!Guid.TryParseExact(offer.SubscriptionId, "N", out var publisherPeerId)) return;
            subscriber = new SubscriberSession(
                receiverSignaling,
                publisherPeerId,
                new ScreenSharing.Client.Media.Codecs.VpxDecoderFactory(),
                displayName: "Alice");
            _ = subscriber.AcceptOfferAsync(offer.Sdp);
            subscriberReady.TrySetResult(subscriber);
        };

        await receiverSignaling.ConnectAsync(wsUri, new ClientHello(Guid.NewGuid(), "Bob", ProtocolVersion.Current), cts.Token);
        await receiverSignaling.JoinRoomAsync(roomId, cts.Token);
        await receiverRoomJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        // Announce the stream — this drives the server to create Bob's subscriber PC.
        await senderSignaling.SendStreamStartedAsync(
            streamId: Guid.NewGuid().ToString("N"),
            kind: StreamKind.Screen,
            hasAudio: false,
            nominalFrameRate: 30,
            ct: cts.Token);

        var readySub = await subscriberReady.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

        var frameCount = 0;
        readySub.Receiver.FrameArrived += (in CaptureFrameData f) => Interlocked.Increment(ref frameCount);

        // --- Pump frames until the viewer decodes at least one. ---
        var bgra = new byte[128 * 128 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = 0x40;
            bgra[i + 1] = 0xA0;
            bgra[i + 2] = 0xE0;
            bgra[i + 3] = 0xFF;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var pumpIndex = 0;
        while (DateTime.UtcNow < deadline && frameCount == 0)
        {
            fakeSource.PumpFrame(bgra, 128, 128, strideBytes: 128 * 4, TimeSpan.FromMilliseconds(pumpIndex++ * 33));
            await Task.Delay(33, cts.Token);
        }

        frameCount.Should().BeGreaterThan(0, "the SFU fan-out should have delivered at least one decoded frame end-to-end");
        capture.EncodedFrameCount.Should().BeGreaterThan(0);
        readySub.Receiver.FramesDecoded.Should().BeGreaterThan(0);
        senderRtc.ConnectionState.Should().Be(RTCPeerConnectionState.connected);

        if (subscriber is not null) await subscriber.DisposeAsync();
        await receiverSignaling.DisposeAsync();
        await senderSignaling.DisposeAsync();
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

    private sealed class FakeCaptureSource : ICaptureSource
    {
        public string DisplayName => "fake";
        public event FrameArrivedHandler? FrameArrived;
        public event TextureArrivedHandler? TextureArrived { add { } remove { } }
#pragma warning disable CS0067 // ICaptureSource contract requires this event even though the fake never closes.
        public event Action? Closed;
#pragma warning restore CS0067
        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void PumpFrame(byte[] pixels, int width, int height, int strideBytes, TimeSpan ts)
        {
            var frame = new CaptureFrameData(
                pixels.AsSpan(0, height * strideBytes),
                width,
                height,
                strideBytes,
                CaptureFramePixelFormat.Bgra8,
                ts);
            FrameArrived?.Invoke(in frame);
        }
    }
}
