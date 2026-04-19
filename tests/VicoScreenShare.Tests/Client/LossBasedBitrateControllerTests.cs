namespace VicoScreenShare.Tests.Client;

using System;
using System.Collections.Generic;
using FluentAssertions;
using VicoScreenShare.Client.Media;

/// <summary>
/// Contract: <see cref="LossBasedBitrateController"/> turns observed
/// fraction-lost values (from inbound RTCP Receiver Reports) into an
/// encoder target bitrate. Three-zone loss response: high loss backs off,
/// moderate loss holds, near-zero loss probes upward toward the configured
/// ceiling. Clock is injectable so tests don't rely on wall time.
/// </summary>
public sealed class LossBasedBitrateControllerTests
{
    [Fact]
    public void Starts_at_target_bitrate()
    {
        var now = TimeSpan.Zero;
        var c = new LossBasedBitrateController(
            targetBitrate: 12_000_000, minBitrate: 500_000, () => now);

        c.CurrentBitrate.Should().Be(12_000_000);
    }

    [Fact]
    public void High_loss_steps_bitrate_down()
    {
        var now = TimeSpan.Zero;
        var c = new LossBasedBitrateController(12_000_000, 500_000, () => now);
        var initial = c.CurrentBitrate;

        now = TimeSpan.FromSeconds(2);
        c.Observe(fractionLost: 0.20); // 20% loss, firmly above threshold

        c.CurrentBitrate.Should().BeLessThan(initial,
            because: "high loss should trigger an immediate back-off");
    }

    [Fact]
    public void Zero_loss_recovers_up_toward_target_over_time()
    {
        var now = TimeSpan.Zero;
        var c = new LossBasedBitrateController(12_000_000, 500_000, () => now);
        // Drop first:
        now += TimeSpan.FromSeconds(2); c.Observe(0.30);
        now += TimeSpan.FromSeconds(2); c.Observe(0.30);
        now += TimeSpan.FromSeconds(2); c.Observe(0.30);
        var lowPoint = c.CurrentBitrate;
        lowPoint.Should().BeLessThan(12_000_000);

        // Then recover:
        for (var i = 0; i < 50; i++)
        {
            now += TimeSpan.FromSeconds(2);
            c.Observe(0.0);
        }
        c.CurrentBitrate.Should().Be(12_000_000,
            because: "after plenty of clean reports, we should be back at target");
    }

    [Fact]
    public void Moderate_loss_neither_steps_up_nor_down()
    {
        var now = TimeSpan.Zero;
        var c = new LossBasedBitrateController(12_000_000, 500_000, () => now);
        // Step down so we're not already pinned at target:
        now += TimeSpan.FromSeconds(2); c.Observe(0.30);
        var after = c.CurrentBitrate;

        // Now feed moderate loss (in the hold zone):
        for (var i = 0; i < 5; i++)
        {
            now += TimeSpan.FromSeconds(2);
            c.Observe(0.05);
        }
        c.CurrentBitrate.Should().Be(after,
            because: "moderate loss is the steady zone — neither back off nor probe up");
    }

    [Fact]
    public void Bitrate_is_clamped_to_the_minimum_floor()
    {
        var now = TimeSpan.Zero;
        var c = new LossBasedBitrateController(12_000_000, 500_000, () => now);
        for (var i = 0; i < 100; i++)
        {
            now += TimeSpan.FromSeconds(2);
            c.Observe(0.50); // catastrophic loss
        }
        c.CurrentBitrate.Should().Be(500_000,
            because: "the controller must never step below the configured floor");
    }

    [Fact]
    public void Successive_observations_within_the_cooldown_do_not_adjust()
    {
        var now = TimeSpan.Zero;
        var c = new LossBasedBitrateController(12_000_000, 500_000, () => now);

        now += TimeSpan.FromSeconds(2);
        c.Observe(0.30);
        var after = c.CurrentBitrate;

        // No time advance — next observe should be a no-op:
        c.Observe(0.30);
        c.CurrentBitrate.Should().Be(after,
            because: "cooldown gate prevents runaway back-off on bursty feedback");
    }

    [Fact]
    public void Bitrate_changed_event_fires_only_on_actual_change()
    {
        var now = TimeSpan.Zero;
        var c = new LossBasedBitrateController(12_000_000, 500_000, () => now);
        var changes = new List<int>();
        c.BitrateChanged += b => changes.Add(b);

        now += TimeSpan.FromSeconds(2); c.Observe(0.30); // down
        now += TimeSpan.FromSeconds(2); c.Observe(0.30); // down
        now += TimeSpan.FromSeconds(2); c.Observe(0.05); // hold
        now += TimeSpan.FromSeconds(2); c.Observe(0.05); // hold

        changes.Count.Should().Be(2,
            because: "only the two down-steps should have fired the event; the holds should not");
    }
}
