namespace VicoScreenShare.Tests.Client;

using System;
using FluentAssertions;
using VicoScreenShare.Client.Media;

/// <summary>
/// Contract: <see cref="SendPacer"/> is a token-bucket rate limiter that caps
/// outgoing bytes per second, absorbing short bursts up to a configurable
/// capacity. It is the send-side shaper that prevents keyframes from firing
/// ~200 RTP packets back-to-back into a kernel UDP buffer too small to hold
/// them. Clock is injected via a callback so the tests run deterministically.
/// </summary>
public sealed class SendPacerTests
{
    [Fact]
    public void Fresh_bucket_permits_an_immediate_burst_up_to_capacity()
    {
        var now = TimeSpan.Zero;
        var pacer = new SendPacer(bytesPerSecond: 10_000, burstBytes: 1_000, () => now);

        pacer.TryConsume(1_000).Should().BeTrue(because: "burst capacity is 1000");
    }

    [Fact]
    public void Draining_the_bucket_blocks_further_sends_until_refill()
    {
        var now = TimeSpan.Zero;
        var pacer = new SendPacer(bytesPerSecond: 10_000, burstBytes: 1_000, () => now);

        pacer.TryConsume(1_000).Should().BeTrue();
        pacer.TryConsume(1).Should().BeFalse(because: "bucket is empty, no time has passed");
    }

    [Fact]
    public void Bucket_refills_at_configured_rate()
    {
        var now = TimeSpan.Zero;
        var pacer = new SendPacer(bytesPerSecond: 10_000, burstBytes: 1_000, () => now);

        pacer.TryConsume(1_000).Should().BeTrue();
        now = TimeSpan.FromMilliseconds(100); // 100 ms → 1000 bytes refilled at 10k/s
        pacer.TryConsume(1_000).Should().BeTrue();
    }

    [Fact]
    public void Bucket_never_exceeds_its_capacity_even_after_a_long_idle()
    {
        var now = TimeSpan.Zero;
        var pacer = new SendPacer(bytesPerSecond: 10_000, burstBytes: 1_000, () => now);

        now = TimeSpan.FromSeconds(60); // plenty of refill, but capped at capacity
        pacer.TryConsume(1_000).Should().BeTrue();
        pacer.TryConsume(1).Should().BeFalse(because: "capacity-capped refill, bucket is still just 1000");
    }

    [Fact]
    public void Estimate_wait_for_returns_time_needed_for_full_refill()
    {
        var now = TimeSpan.Zero;
        var pacer = new SendPacer(bytesPerSecond: 10_000, burstBytes: 1_000, () => now);

        pacer.TryConsume(1_000).Should().BeTrue();
        var wait = pacer.EstimateWaitFor(500);
        wait.Should().BeCloseTo(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Estimate_wait_for_is_zero_when_enough_tokens_are_already_available()
    {
        var now = TimeSpan.Zero;
        var pacer = new SendPacer(bytesPerSecond: 10_000, burstBytes: 1_000, () => now);

        pacer.EstimateWaitFor(500).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Single_consume_larger_than_capacity_never_fits()
    {
        // Callers must size burstBytes above the largest expected single
        // frame. The pacer does not overdraft — it just reports "never" for
        // requests bigger than the capacity so the caller can reconfigure.
        var now = TimeSpan.Zero;
        var pacer = new SendPacer(bytesPerSecond: 10_000, burstBytes: 1_000, () => now);

        pacer.TryConsume(2_000).Should().BeFalse();
        pacer.EstimateWaitFor(2_000).Should().Be(TimeSpan.MaxValue,
            because: "2000 > burst capacity 1000; no amount of waiting will make it fit");
    }

    [Fact]
    public void Rate_of_zero_or_negative_is_treated_as_unlimited()
    {
        var now = TimeSpan.Zero;
        var pacer = new SendPacer(bytesPerSecond: 0, burstBytes: 0, () => now);

        pacer.TryConsume(10_000_000).Should().BeTrue(because: "disabled pacer acts as passthrough");
        pacer.EstimateWaitFor(10_000_000).Should().Be(TimeSpan.Zero);
    }
}
