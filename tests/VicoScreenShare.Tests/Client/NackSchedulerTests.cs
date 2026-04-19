namespace VicoScreenShare.Tests.Client;

using System;
using FluentAssertions;
using VicoScreenShare.Client.Media;

/// <summary>
/// Contract: <see cref="NackScheduler"/> watches the sequence numbers of
/// received RTP packets and, when a gap appears, holds it for a short
/// debounce window before reporting it as NACK-worthy. The debounce keeps
/// us from firing NACKs for out-of-order packets that are about to arrive
/// anyway (jitter). If the missing packet arrives before the debounce
/// expires, no NACK is issued. Clock is injectable for deterministic tests.
/// </summary>
public sealed class NackSchedulerTests
{
    [Fact]
    public void Sequential_arrivals_never_produce_a_nack()
    {
        var now = TimeSpan.Zero;
        var scheduler = new NackScheduler(debounce: TimeSpan.FromMilliseconds(20), () => now);

        scheduler.Observe(100);
        scheduler.Observe(101);
        scheduler.Observe(102);
        now = TimeSpan.FromSeconds(1);

        scheduler.PollReady().Should().BeEmpty();
    }

    [Fact]
    public void Gap_reported_after_debounce_elapses()
    {
        var now = TimeSpan.Zero;
        var scheduler = new NackScheduler(debounce: TimeSpan.FromMilliseconds(20), () => now);

        scheduler.Observe(100);
        scheduler.Observe(103); // missing 101, 102

        now = TimeSpan.FromMilliseconds(10);
        scheduler.PollReady().Should().BeEmpty(because: "still within debounce window");

        now = TimeSpan.FromMilliseconds(30);
        scheduler.PollReady().Should().Equal(new ushort[] { 101, 102 });
    }

    [Fact]
    public void Nothing_returned_twice_for_the_same_gap()
    {
        var now = TimeSpan.Zero;
        var scheduler = new NackScheduler(debounce: TimeSpan.FromMilliseconds(20), () => now);

        scheduler.Observe(100);
        scheduler.Observe(103);
        now = TimeSpan.FromMilliseconds(30);

        scheduler.PollReady().Should().Equal(new ushort[] { 101, 102 });
        scheduler.PollReady().Should().BeEmpty(because: "NACK already emitted for these seqs");
    }

    [Fact]
    public void Late_arrival_before_debounce_cancels_the_nack()
    {
        var now = TimeSpan.Zero;
        var scheduler = new NackScheduler(debounce: TimeSpan.FromMilliseconds(20), () => now);

        scheduler.Observe(100);
        scheduler.Observe(102); // 101 is missing

        now = TimeSpan.FromMilliseconds(10);
        scheduler.Observe(101); // arrived in time

        now = TimeSpan.FromMilliseconds(30);
        scheduler.PollReady().Should().BeEmpty(because: "the missing packet was delivered before debounce");
    }

    [Fact]
    public void Only_expired_seqs_are_returned_even_when_newer_gaps_exist()
    {
        var now = TimeSpan.Zero;
        var scheduler = new NackScheduler(debounce: TimeSpan.FromMilliseconds(20), () => now);

        scheduler.Observe(100);
        scheduler.Observe(102); // missing 101 at t=0

        now = TimeSpan.FromMilliseconds(25);
        scheduler.Observe(105); // missing 103, 104 at t=25

        now = TimeSpan.FromMilliseconds(30);
        // 101 is past debounce (30-0=30 >= 20); 103/104 are not (30-25=5 < 20).
        scheduler.PollReady().Should().Equal(new ushort[] { 101 });

        now = TimeSpan.FromMilliseconds(50);
        scheduler.PollReady().Should().Equal(new ushort[] { 103, 104 });
    }

    [Fact]
    public void Wraparound_gap_is_reported_across_the_seq_boundary()
    {
        var now = TimeSpan.Zero;
        var scheduler = new NackScheduler(debounce: TimeSpan.FromMilliseconds(20), () => now);

        scheduler.Observe(65534);
        scheduler.Observe(1); // missing 65535, 0

        now = TimeSpan.FromMilliseconds(30);
        scheduler.PollReady().Should().Equal(new ushort[] { 65535, 0 });
    }

    [Fact]
    public void Reordered_packet_arriving_ahead_does_not_create_a_phantom_gap()
    {
        var now = TimeSpan.Zero;
        var scheduler = new NackScheduler(debounce: TimeSpan.FromMilliseconds(20), () => now);

        scheduler.Observe(100);
        scheduler.Observe(101);
        scheduler.Observe(99); // reordered late arrival of a seq we already passed

        now = TimeSpan.FromMilliseconds(30);
        scheduler.PollReady().Should().BeEmpty();
    }

    [Fact]
    public void Reset_clears_all_state()
    {
        var now = TimeSpan.Zero;
        var scheduler = new NackScheduler(debounce: TimeSpan.FromMilliseconds(20), () => now);

        scheduler.Observe(100);
        scheduler.Observe(103);
        scheduler.Reset();
        now = TimeSpan.FromMilliseconds(30);

        scheduler.PollReady().Should().BeEmpty();
    }
}
