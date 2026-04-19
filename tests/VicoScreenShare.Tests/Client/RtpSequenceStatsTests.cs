namespace VicoScreenShare.Tests.Client;

using FluentAssertions;
using VicoScreenShare.Client.Media;

/// <summary>
/// Contract: RTP sequence tracker counts lost packets via RFC 3550 §A.3 —
/// <c>expected - received</c>, where <c>expected</c> is derived from the
/// highest seq seen (with 16-bit rollover). Reorders MUST NOT inflate
/// reported loss; a straggler that arrives after a forward jump should
/// unwind the earlier overcount.
/// </summary>
public sealed class RtpSequenceStatsTests
{
    private const uint Ssrc = 0xDEADBEEF;

    [Fact]
    public void No_loss_when_packets_arrive_in_order()
    {
        var s = new RtpSequenceStats();
        for (ushort i = 100; i < 120; i++)
        {
            s.Observe(Ssrc, i);
        }
        s.Received.Should().Be(20);
        s.Lost.Should().Be(0);
        s.LossPercent.Should().Be(0.0);
    }

    [Fact]
    public void Forward_gap_reports_loss()
    {
        var s = new RtpSequenceStats();
        s.Observe(Ssrc, 100);
        s.Observe(Ssrc, 105); // 4 lost: 101,102,103,104
        s.Received.Should().Be(2);
        s.Lost.Should().Be(4);
    }

    [Fact]
    public void Reordered_arrival_unwinds_earlier_overcount()
    {
        // The whole point of RFC-3550 derived accounting: if we report
        // 4 lost after seeing 100, 105, then later receive 101, 102, 103,
        // 104 as reorders, loss must drop back toward 0. The old
        // forward-gap counter never walked back from the initial 4.
        var s = new RtpSequenceStats();
        s.Observe(Ssrc, 100);
        s.Observe(Ssrc, 105);
        s.Lost.Should().Be(4);

        s.Observe(Ssrc, 101);
        s.Observe(Ssrc, 102);
        s.Observe(Ssrc, 103);
        s.Observe(Ssrc, 104);

        s.Received.Should().Be(6);
        s.Lost.Should().Be(0, because: "all stragglers arrived");
        s.LossPercent.Should().Be(0.0);
    }

    [Fact]
    public void Partial_reorder_leaves_residual_loss()
    {
        var s = new RtpSequenceStats();
        s.Observe(Ssrc, 100);
        s.Observe(Ssrc, 105);
        s.Observe(Ssrc, 102); // one straggler in
        // Still missing 101, 103, 104.
        s.Received.Should().Be(3);
        s.Lost.Should().Be(3);
    }

    [Fact]
    public void Sixteen_bit_wrap_is_handled()
    {
        var s = new RtpSequenceStats();
        // Approach the wrap without loss.
        s.Observe(Ssrc, 65530);
        s.Observe(Ssrc, 65531);
        s.Observe(Ssrc, 65532);
        s.Observe(Ssrc, 65533);
        s.Observe(Ssrc, 65534);
        s.Observe(Ssrc, 65535);
        // Wrap.
        s.Observe(Ssrc, 0);
        s.Observe(Ssrc, 1);
        s.Observe(Ssrc, 2);
        s.Received.Should().Be(9);
        s.Lost.Should().Be(0);
    }

    [Fact]
    public void Ssrc_change_resets_tracking()
    {
        var s = new RtpSequenceStats();
        s.Observe(0x11111111, 100);
        s.Observe(0x11111111, 105); // 4 lost on old SSRC
        s.Lost.Should().Be(4);

        // New SSRC — publisher restart or SFU rekey. Counters reset; the
        // first packet under the new SSRC becomes the new baseline.
        s.Observe(0x22222222, 500);
        s.Observe(0x22222222, 501);
        s.Received.Should().Be(2);
        s.Lost.Should().Be(0);
    }
}
