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
        var samples = new List<(uint duration, byte[] payload, TimeSpan ts)>();
        using var streamer = new CaptureStreamer(source, (d, p, t) => samples.Add((d, p, t)), new VideoSettings());
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
    public void Propagates_content_timestamp_to_the_onEncoded_callback()
    {
        var source = new FakeCaptureSource();
        var samples = new List<(uint duration, byte[] payload, TimeSpan ts)>();
        using var streamer = new CaptureStreamer(source, (d, p, t) => samples.Add((d, p, t)), new VideoSettings());
        streamer.Start();

        const int width = 64;
        const int height = 64;
        var bgra = new byte[width * height * 4];

        // Pump 6 frames 50ms apart. VP8 is sync so the callback ts should
        // equal the input ts verbatim for every emitted frame.
        var inputs = new List<TimeSpan>();
        for (var i = 0; i < 6; i++)
        {
            var ts = TimeSpan.FromMilliseconds(50 * i);
            inputs.Add(ts);
            source.PumpFrame(bgra, width, height, strideBytes: width * 4, ts);
        }

        samples.Should().NotBeEmpty("sync VP8 should emit content for every input");
        foreach (var sample in samples)
        {
            inputs.Should().Contain(sample.ts,
                "every callback timestamp should map back to one of the submitted inputs");
        }
    }

    [Fact]
    public void Derives_width_from_source_aspect_when_target_height_is_set()
    {
        var source = new FakeCaptureSource();
        var settings = new VideoSettings
        {
            TargetHeight = 360,
            TargetFrameRate = 30,
        };
        using var streamer = new CaptureStreamer(source, (_, _, _) => { }, settings);
        streamer.Start();

        // Source frame is 1280x720 (16:9). Target height 360 → width should be
        // derived as 640 to preserve aspect.
        const int width = 1280;
        const int height = 720;
        var bgra = new byte[width * height * 4];
        source.PumpFrame(bgra, width, height, strideBytes: width * 4, TimeSpan.FromMilliseconds(0));

        streamer.CurrentEncoderWidth.Should().Be(640);
        streamer.CurrentEncoderHeight.Should().Be(360);
    }

    [Fact]
    public void Preserves_ultrawide_aspect_when_downscaling()
    {
        var source = new FakeCaptureSource();
        var settings = new VideoSettings
        {
            TargetHeight = 720,
            TargetFrameRate = 30,
        };
        using var streamer = new CaptureStreamer(source, (_, _, _) => { }, settings);
        streamer.Start();

        // 3440x1440 ultrawide → 720 target height → width should be 1720.
        const int width = 3440;
        const int height = 1440;
        var bgra = new byte[width * height * 4];
        source.PumpFrame(bgra, width, height, strideBytes: width * 4, TimeSpan.FromMilliseconds(0));

        streamer.CurrentEncoderHeight.Should().Be(720);
        streamer.CurrentEncoderWidth.Should().Be(1720);
    }

    [Fact]
    public void Stop_detaches_from_source_and_halts_emission()
    {
        var source = new FakeCaptureSource();
        var count = 0;
        using var streamer = new CaptureStreamer(source, (_, _, _) => Interlocked.Increment(ref count), new VideoSettings());
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
        public event TextureArrivedHandler? TextureArrived { add { } remove { } }
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
