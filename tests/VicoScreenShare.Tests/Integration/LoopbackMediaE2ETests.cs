namespace VicoScreenShare.Tests.Integration;

using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Services;
using VicoScreenShare.Protocol;
using VicoScreenShare.Protocol.Messages;
using VicoScreenShare.Server;

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
            if (string.IsNullOrEmpty(offer.SubscriptionId))
            {
                return;
            }

            if (!Guid.TryParseExact(offer.SubscriptionId, "N", out var publisherPeerId))
            {
                return;
            }

            subscriber = new SubscriberSession(
                receiverSignaling,
                publisherPeerId,
                new VicoScreenShare.Client.Media.Codecs.VpxDecoderFactory(),
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

        if (subscriber is not null)
        {
            await subscriber.DisposeAsync();
        }

        await receiverSignaling.DisposeAsync();
        await senderSignaling.DisposeAsync();
    }

    [Fact]
    public async Task Publisher_and_viewer_exchange_opus_audio_through_the_sfu()
    {
        // Audio-track end-to-end proof. Mirrors the video round-trip
        // above, but pumps a 440 Hz sine through AudioStreamer →
        // RTCPeerConnection.SendAudio → SFU → subscriber's AudioReceiver.
        // Asserts the viewer's renderer saw decoded PCM frames with the
        // right shape. Shape only, not frequency — decode/render cadence
        // is the dependency we want to lock in; the codec-side frequency
        // survival is already covered by OpusAudioEncoderTests.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        await using var server = await StartServerAsync();
        var wsUri = ResolveWsUri(server);

        var senderSignaling = new SignalingClient();
        var senderRoomJoined = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
        senderSignaling.RoomJoined += j => senderRoomJoined.TrySetResult(j);

        await senderSignaling.ConnectAsync(wsUri, new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current), cts.Token);
        await senderSignaling.CreateRoomAsync(cts.Token);
        var senderJoin = await senderRoomJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        var roomId = senderJoin.RoomId;

        await using var senderRtc = new WebRtcSession(senderSignaling, WebRtcRole.Sender);
        await senderRtc.NegotiateAsync(TimeSpan.FromSeconds(15));

        var codecs = new OpusAudioCodecFactory();
        var audioSource = new FakeAudioCaptureSource();
        using var resampler = new PassThroughS16Resampler();
        using var audioStreamer = new AudioStreamer(
            audioSource,
            resampler,
            (duration, payload, _) => senderRtc.PeerConnection.SendAudio(duration, payload),
            new AudioSettings { Stereo = true, TargetBitrate = 96_000 },
            codecs);
        audioStreamer.Start();

        var receiverSignaling = new SignalingClient();
        var receiverRoomJoined = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverSignaling.RoomJoined += j => receiverRoomJoined.TrySetResult(j);

        SubscriberSession? subscriber = null;
        var fakeRenderer = new RecordingAudioRenderer();
        var subscriberReady = new TaskCompletionSource<SubscriberSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverSignaling.SdpOfferReceived += offer =>
        {
            if (string.IsNullOrEmpty(offer.SubscriptionId))
            {
                return;
            }
            if (!Guid.TryParseExact(offer.SubscriptionId, "N", out var publisherPeerId))
            {
                return;
            }

            subscriber = new SubscriberSession(
                receiverSignaling,
                publisherPeerId,
                new VicoScreenShare.Client.Media.Codecs.VpxDecoderFactory(),
                displayName: "Alice",
                iceServers: null,
                audioDecoderFactory: codecs,
                audioRenderer: fakeRenderer);
            _ = subscriber.AcceptOfferAsync(offer.Sdp);
            subscriberReady.TrySetResult(subscriber);
        };

        await receiverSignaling.ConnectAsync(wsUri, new ClientHello(Guid.NewGuid(), "Bob", ProtocolVersion.Current), cts.Token);
        await receiverSignaling.JoinRoomAsync(roomId, cts.Token);
        await receiverRoomJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        await senderSignaling.SendStreamStartedAsync(
            streamId: Guid.NewGuid().ToString("N"),
            kind: StreamKind.Screen,
            hasAudio: true,
            nominalFrameRate: 30,
            ct: cts.Token);

        var readySub = await subscriberReady.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

        // Pump 40 × 20 ms = 800 ms of 440 Hz sine stereo into the capture
        // source. AudioStreamer slices into 960-sample frames, encodes
        // each, SendAudio blasts them across, SFU forwards, subscriber
        // decodes, renderer records. With a 3-slot jitter buffer the
        // first 3 frames get held back, so we need at least 4 to see any
        // output — pumping 40 gives plenty of margin.
        const int channels = 2;
        const int frameSamples = 960;
        var frame = new short[frameSamples * channels];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var basePos = 0;
        while (DateTime.UtcNow < deadline && fakeRenderer.FramesSubmitted < 4)
        {
            for (var i = 0; i < frameSamples; i++)
            {
                var phase = 2.0 * Math.PI * 440.0 * (basePos + i) / 48000.0;
                var s = (short)(Math.Sin(phase) * 8000.0);
                frame[i * channels + 0] = s;
                frame[i * channels + 1] = s;
            }
            basePos += frameSamples;
            audioSource.PumpFrame(frame, sampleRate: 48000, channels: channels, timestamp: TimeSpan.FromMilliseconds(basePos / 48.0));
            await Task.Delay(20, cts.Token);
        }

        audioStreamer.FramesEmitted.Should().BeGreaterThan(0, "publisher must have emitted encoded audio");
        readySub.AudioReceiver.Should().NotBeNull("subscriber session must have built an audio receiver");
        readySub.AudioReceiver!.PacketsReceived.Should().BeGreaterThan(0, "audio RTP must have crossed the SFU");
        fakeRenderer.FramesSubmitted.Should().BeGreaterThan(0, "decoder must have produced PCM out the other end");
        fakeRenderer.ObservedSampleRate.Should().Be(48000);
        fakeRenderer.ObservedChannels.Should().Be(2);

        if (subscriber is not null)
        {
            await subscriber.DisposeAsync();
        }
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

    /// <summary>Test-only audio source that emits pre-built PCM frames
    /// on demand. Used by the audio round-trip test in lieu of
    /// WASAPI — the integration test runs on CI without a default
    /// render endpoint, so a fake source is the only reliable path.
    /// </summary>
    private sealed class FakeAudioCaptureSource : IAudioCaptureSource
    {
        public string DisplayName => "fake-audio";
        public int SourceSampleRate => 48000;
        public int SourceChannels => 2;
        public AudioSampleFormat SourceFormat => AudioSampleFormat.PcmS16Interleaved;
        public event AudioFrameArrivedHandler? FrameArrived;
#pragma warning disable CS0067
        public event Action? Closed;
#pragma warning restore CS0067

        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void PumpFrame(short[] interleavedS16, int sampleRate, int channels, TimeSpan timestamp)
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleavedS16.AsSpan());
            var frame = new AudioFrameData(bytes, sampleRate, channels, AudioSampleFormat.PcmS16Interleaved, timestamp);
            FrameArrived?.Invoke(in frame);
        }
    }

    private sealed class PassThroughS16Resampler : IAudioResampler
    {
        public int Resample(ReadOnlySpan<byte> inputPcm, int inputSampleRate, int inputChannels, AudioSampleFormat inputFormat, Span<short> destination)
        {
            if (inputFormat != AudioSampleFormat.PcmS16Interleaved)
            {
                throw new NotSupportedException("test pass-through supports S16 only");
            }
            var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(inputPcm);
            src.CopyTo(destination);
            return src.Length;
        }

        public void Dispose() { }
    }

    /// <summary>Test-only renderer that counts submitted frames and
    /// echoes the first-seen sample rate / channel count. Does not
    /// actually play audio.</summary>
    private sealed class RecordingAudioRenderer : IAudioRenderer
    {
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public double Volume { get; set; } = 1.0;
        public int ObservedSampleRate { get; private set; }
        public int ObservedChannels { get; private set; }
        public int FramesSubmitted { get; private set; }

        public Task StartAsync(int sampleRate, int channels)
        {
            SampleRate = sampleRate;
            Channels = channels;
            ObservedSampleRate = sampleRate;
            ObservedChannels = channels;
            return Task.CompletedTask;
        }

        public void Submit(ReadOnlySpan<short> interleavedPcm, TimeSpan timestamp)
        {
            FramesSubmitted++;
        }

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
