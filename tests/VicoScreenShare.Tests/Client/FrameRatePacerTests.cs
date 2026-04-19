namespace VicoScreenShare.Tests.Client;

using FluentAssertions;
using VicoScreenShare.Client.Media;

public class FrameRatePacerTests
{
    [Fact]
    public void First_frame_is_always_admitted_and_anchors_the_clock()
    {
        var pacer = new FrameRatePacer(60);
        pacer.ShouldAccept(TimeSpan.FromMilliseconds(12345.678))
            .Should().BeTrue("the very first call always admits and anchors at that timestamp");
        pacer.AcceptedCount.Should().Be(1);
    }

    [Fact]
    public void Steady_source_at_target_rate_admits_every_frame()
    {
        var pacer = new FrameRatePacer(60);
        var gap = 1000.0 / 60.0; // ≈ 16.666... ms

        for (var i = 0; i < 60; i++)
        {
            var ts = TimeSpan.FromMilliseconds(i * gap);
            pacer.ShouldAccept(ts).Should().BeTrue($"frame {i} at {ts.TotalMilliseconds:F2}ms is exactly on-schedule");
        }

        pacer.AcceptedCount.Should().Be(60);
    }

    [Fact]
    public void Fast_source_is_capped_to_target_rate()
    {
        // 120 fps source, 60 fps target → accepts every other frame.
        var pacer = new FrameRatePacer(60);
        var fastGap = 1000.0 / 120.0; // ~8.33 ms

        var accepted = 0;
        for (var i = 0; i < 120; i++)
        {
            if (pacer.ShouldAccept(TimeSpan.FromMilliseconds(i * fastGap)))
            {
                accepted++;
            }
        }

        // 120 input frames / 2 = 60 accepted. Allow off-by-one for the
        // first-frame-always-admit edge case (+1 if the 120th frame lands
        // exactly on the credit boundary).
        accepted.Should().BeInRange(59, 61,
            "a 120 fps source paced to 60 fps should yield ~60 accepted frames in 120 arrivals");
    }

    [Fact]
    public void Slow_source_admits_everything()
    {
        // 30 fps source, 60 fps target → accepts every frame (we can't
        // create frames we don't have).
        var pacer = new FrameRatePacer(60);
        var slowGap = 1000.0 / 30.0; // ~33.33 ms

        for (var i = 0; i < 30; i++)
        {
            pacer.ShouldAccept(TimeSpan.FromMilliseconds(i * slowGap))
                .Should().BeTrue($"slow source at frame {i} should always admit");
        }

        pacer.AcceptedCount.Should().Be(30);
    }

    [Fact]
    public void Reset_clears_the_anchor_so_a_fresh_sequence_starts_from_zero()
    {
        var pacer = new FrameRatePacer(60);
        // Push the anchor far out so a fresh sequence would be "in the past".
        pacer.ShouldAccept(TimeSpan.FromSeconds(100));
        pacer.ShouldAccept(TimeSpan.FromSeconds(100.0167));
        pacer.AcceptedCount.Should().BeGreaterThan(0);

        pacer.Reset();
        pacer.AcceptedCount.Should().Be(0);

        pacer.ShouldAccept(TimeSpan.Zero).Should().BeTrue("after Reset the next call is the new first frame");
        pacer.AcceptedCount.Should().Be(1);
    }

    [Fact]
    public void Bursty_delivery_caps_to_target_rate_in_aggregate()
    {
        // Simulate a WGC-style burst: 10 frames crammed into 10 ms, then
        // a long pause. The pacer should admit only the ones that fit
        // the credit budget, roughly 1 frame every 16.666 ms of content
        // time.
        var pacer = new FrameRatePacer(60);
        var accepted = 0;

        // Burst of 10 arrivals, 1 ms apart.
        for (var i = 0; i < 10; i++)
        {
            if (pacer.ShouldAccept(TimeSpan.FromMilliseconds(i))) accepted++;
        }

        // The first frame always admits; subsequent 1-ms-apart frames
        // are all earlier than the credit budget and get dropped.
        accepted.Should().Be(1, "only the first frame of the 1-ms-apart burst should admit at 60 fps target");
    }
}
