using FluentAssertions;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Platform;

namespace ScreenSharing.Tests.Client;

public class CaptureStreamerTests
{
    [Fact]
    public void Emits_encoded_payload_when_fed_bgra_frames()
    {
        var source = new FakeCaptureSource();
        var samples = new List<(uint duration, byte[] payload)>();
        using var streamer = new CaptureStreamer(source, (d, p) => samples.Add((d, p)), new VideoSettings());
        streamer.Start();

        // 64x64 is small enough to stay fast in a unit test but big enough that
        // VpxVideoEncoder emits at least one keyframe with a non-trivial payload.
        const int width = 64;
        const int height = 64;
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = 0x20; // B
            bgra[i + 1] = 0x80; // G
            bgra[i + 2] = 0xC0; // R
            bgra[i + 3] = 0xFF;
        }

        // Pump a handful of frames; VP8 typically produces bytes on the first
        // keyframe but we allow several attempts in case the encoder has warm-up.
        // 50ms spacing = 20fps, comfortably above the default 30fps throttle floor.
        for (var frameIndex = 0; frameIndex < 5; frameIndex++)
        {
            source.PumpFrame(bgra, width, height, strideBytes: width * 4, TimeSpan.FromMilliseconds(50 * frameIndex));
        }

        streamer.FrameCount.Should().Be(5);
        streamer.EncodedFrameCount.Should().BeGreaterThan(0,
            "VpxVideoEncoder should emit at least one keyframe within the first few frames");
        samples.Should().NotBeEmpty();
        samples.First().payload.Length.Should().BeGreaterThan(0);
        samples.First().duration.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Respects_encoder_resolution_cap_from_video_settings()
    {
        var source = new FakeCaptureSource();
        var settings = new VideoSettings
        {
            MaxEncoderWidth = 640,
            MaxEncoderHeight = 360,
            TargetFrameRate = 30,
        };
        using var streamer = new CaptureStreamer(source, (_, _) => { }, settings);
        streamer.Start();

        // Source frame is 1280x720 (double the cap in each axis).
        const int width = 1280;
        const int height = 720;
        var bgra = new byte[width * height * 4];
        source.PumpFrame(bgra, width, height, strideBytes: width * 4, TimeSpan.FromMilliseconds(0));

        streamer.CurrentEncoderWidth.Should().Be(640);
        streamer.CurrentEncoderHeight.Should().Be(360);
    }

    [Fact]
    public void Drops_frames_when_incoming_rate_exceeds_target_fps()
    {
        var source = new FakeCaptureSource();
        var settings = new VideoSettings
        {
            MaxEncoderWidth = 1280,
            MaxEncoderHeight = 720,
            TargetFrameRate = 30,
        };
        using var streamer = new CaptureStreamer(source, (_, _) => { }, settings);
        streamer.Start();

        const int width = 64;
        const int height = 64;
        var bgra = new byte[width * height * 4];

        // Pump 50 frames 10 ms apart → 100 fps incoming. With a 30 fps target
        // the throttle should let through ~15 of them (gap ≈ 33.3 ms).
        for (var i = 0; i < 50; i++)
        {
            source.PumpFrame(bgra, width, height, strideBytes: width * 4, TimeSpan.FromMilliseconds(i * 10));
        }

        streamer.FrameCount.Should().BeLessThan(20,
            "50 frames at 10 ms spacing should be throttled down to ~15 at 30 fps");
        streamer.FrameCount.Should().BeGreaterThan(10,
            "throttle should still admit roughly one frame per 33 ms window");
    }

    [Fact]
    public void Stop_detaches_from_source_and_halts_emission()
    {
        var source = new FakeCaptureSource();
        var count = 0;
        using var streamer = new CaptureStreamer(source, (_, _) => Interlocked.Increment(ref count), new VideoSettings());
        streamer.Start();

        var bgra = new byte[32 * 32 * 4];
        source.PumpFrame(bgra, 32, 32, strideBytes: 128, TimeSpan.FromMilliseconds(0));

        streamer.Stop();

        // Further frames from the source should not reach the encoder.
        source.PumpFrame(bgra, 32, 32, strideBytes: 128, TimeSpan.FromMilliseconds(33));
        source.PumpFrame(bgra, 32, 32, strideBytes: 128, TimeSpan.FromMilliseconds(66));

        streamer.FrameCount.Should().Be(1);
    }

    private sealed class FakeCaptureSource : ICaptureSource
    {
        public string DisplayName => "fake";
        public event FrameArrivedHandler? FrameArrived;
        public event Action? Closed;
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

        public void RaiseClosed() => Closed?.Invoke();
    }
}
