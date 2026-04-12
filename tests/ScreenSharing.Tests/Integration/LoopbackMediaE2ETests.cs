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
/// Phase 3.5 end-to-end proof: spin up a real server host, connect two clients,
/// have one publish VP8 via <see cref="CaptureStreamer"/>, and verify the other
/// receives and decodes the frames through the SFU fan-out + <see cref="StreamReceiver"/>.
/// Exercises the full pipeline from captured BGRA pixels through I420 -> VP8
/// encoder -> RTP -> server forwarder -> RTP -> VP8 decoder -> BGRA.
/// </summary>
public sealed class LoopbackMediaE2ETests
{
    [Fact]
    public async Task Sender_and_receiver_exchange_vp8_frames_through_the_sfu()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        await using var server = await StartServerAsync();
        var wsUri = ResolveWsUri(server);

        // --- Sender side: create room, negotiate sender PC, attach CaptureStreamer. ---
        var senderSignaling = new SignalingClient();
        var senderRoomJoined = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
        senderSignaling.RoomJoined += j => senderRoomJoined.TrySetResult(j);

        await senderSignaling.ConnectAsync(wsUri, new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current), cts.Token);
        await senderSignaling.CreateRoomAsync(null, cts.Token);
        var senderJoin = await senderRoomJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        var roomId = senderJoin.RoomId;

        await using var senderRtc = new WebRtcSession(senderSignaling, WebRtcRole.Sender);
        await senderRtc.NegotiateAsync(TimeSpan.FromSeconds(15));

        var fakeSource = new FakeCaptureSource();
        using var capture = new CaptureStreamer(
            fakeSource,
            (duration, payload) => senderRtc.PeerConnection.SendVideo(duration, payload));
        capture.Start();

        // --- Receiver side: join room, negotiate receiver PC, attach StreamReceiver. ---
        var receiverSignaling = new SignalingClient();
        var receiverRoomJoined = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverSignaling.RoomJoined += j => receiverRoomJoined.TrySetResult(j);

        await receiverSignaling.ConnectAsync(wsUri, new ClientHello(Guid.NewGuid(), "Bob", ProtocolVersion.Current), cts.Token);
        await receiverSignaling.JoinRoomAsync(roomId, null, cts.Token);
        await receiverRoomJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        await using var receiverRtc = new WebRtcSession(receiverSignaling, WebRtcRole.Receiver);
        await receiverRtc.NegotiateAsync(TimeSpan.FromSeconds(15));

        using var stream = new StreamReceiver(receiverRtc.PeerConnection, displayName: "Alice");
        var frameCount = 0;
        stream.FrameArrived += (in CaptureFrameData f) => Interlocked.Increment(ref frameCount);
        await stream.StartAsync();

        // --- Pump frames until the receiver decodes at least one. ---
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
        stream.FramesDecoded.Should().BeGreaterThan(0);
        senderRtc.ConnectionState.Should().Be(RTCPeerConnectionState.connected);
        receiverRtc.ConnectionState.Should().Be(RTCPeerConnectionState.connected);

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
