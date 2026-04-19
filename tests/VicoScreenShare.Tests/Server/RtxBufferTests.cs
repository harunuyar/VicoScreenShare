namespace VicoScreenShare.Tests.Server;

using FluentAssertions;
using VicoScreenShare.Server.Sfu;

/// <summary>
/// Contract: <see cref="RtxBuffer"/> is a tiny ring buffer of recent outbound
/// RTP packets keyed by sequence number, so the SFU can reply to a NACK from
/// a viewer without ever round-tripping to the upstream publisher. Capacity
/// is bounded; the oldest entry is evicted on overflow. The buffer stores
/// whatever's needed to re-issue the packet verbatim via
/// <c>SendRtpRaw</c>: the payload bytes plus the RTP header fields that
/// SIPSorcery wants back (timestamp, marker, payload type, seq).
/// </summary>
public sealed class RtxBufferTests
{
    [Fact]
    public void Recorded_packet_can_be_looked_up_by_sequence_number()
    {
        var buffer = new RtxBuffer(capacity: 4);
        var payload = new byte[] { 1, 2, 3 };
        buffer.Record(seq: 10, payload, timestamp: 1000, payloadType: 96, marker: false);

        buffer.TryGet(10, out var entry).Should().BeTrue();
        entry.Seq.Should().Be((ushort)10);
        entry.Payload.Should().Equal(payload);
        entry.Timestamp.Should().Be(1000u);
        entry.PayloadType.Should().Be(96);
        entry.Marker.Should().BeFalse();
    }

    [Fact]
    public void Missing_sequence_number_reports_not_found()
    {
        var buffer = new RtxBuffer(capacity: 4);
        buffer.Record(seq: 10, new byte[] { 1 }, timestamp: 1000, payloadType: 96, marker: false);

        buffer.TryGet(999, out var _).Should().BeFalse();
    }

    [Fact]
    public void Oldest_entries_are_evicted_once_capacity_is_exceeded()
    {
        var buffer = new RtxBuffer(capacity: 2);
        buffer.Record(seq: 10, new byte[] { 1 }, timestamp: 100, payloadType: 96, marker: false);
        buffer.Record(seq: 11, new byte[] { 2 }, timestamp: 200, payloadType: 96, marker: false);
        buffer.Record(seq: 12, new byte[] { 3 }, timestamp: 300, payloadType: 96, marker: false);

        buffer.TryGet(10, out var _).Should().BeFalse(because: "oldest was evicted");
        buffer.TryGet(11, out var e11).Should().BeTrue();
        buffer.TryGet(12, out var e12).Should().BeTrue();
        e11.Payload.Should().Equal(2);
        e12.Payload.Should().Equal(3);
    }

    [Fact]
    public void Wraparound_sequence_numbers_are_stored_distinctly()
    {
        // RTP seq is a ushort that wraps from 65535 → 0 during long sessions.
        var buffer = new RtxBuffer(capacity: 4);
        buffer.Record(seq: 65534, new byte[] { 10 }, timestamp: 1, payloadType: 96, marker: false);
        buffer.Record(seq: 65535, new byte[] { 11 }, timestamp: 2, payloadType: 96, marker: false);
        buffer.Record(seq: 0, new byte[] { 12 }, timestamp: 3, payloadType: 96, marker: false);
        buffer.Record(seq: 1, new byte[] { 13 }, timestamp: 4, payloadType: 96, marker: false);

        buffer.TryGet(65534, out var a).Should().BeTrue(); a.Payload.Should().Equal(10);
        buffer.TryGet(65535, out var b).Should().BeTrue(); b.Payload.Should().Equal(11);
        buffer.TryGet(0, out var c).Should().BeTrue(); c.Payload.Should().Equal(12);
        buffer.TryGet(1, out var d).Should().BeTrue(); d.Payload.Should().Equal(13);
    }

    [Fact]
    public void Recording_same_sequence_twice_keeps_the_latest_payload()
    {
        // Pathological but worth documenting — if the caller re-records a seq,
        // latest wins. Avoids accidental mix-ups if a NACK handler re-injects
        // a packet into the buffer.
        var buffer = new RtxBuffer(capacity: 4);
        buffer.Record(seq: 10, new byte[] { 1 }, timestamp: 100, payloadType: 96, marker: false);
        buffer.Record(seq: 10, new byte[] { 99 }, timestamp: 200, payloadType: 96, marker: true);

        buffer.TryGet(10, out var entry).Should().BeTrue();
        entry.Payload.Should().Equal(99);
        entry.Timestamp.Should().Be(200u);
        entry.Marker.Should().BeTrue();
    }

    [Fact]
    public void Capacity_of_zero_or_negative_disables_the_buffer()
    {
        var buffer = new RtxBuffer(capacity: 0);
        buffer.Record(seq: 10, new byte[] { 1 }, timestamp: 100, payloadType: 96, marker: false);

        buffer.TryGet(10, out var _).Should().BeFalse(because: "zero capacity means nothing is retained");
    }
}
