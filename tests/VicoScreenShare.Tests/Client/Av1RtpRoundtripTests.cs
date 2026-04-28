namespace VicoScreenShare.Tests.Client;

using FluentAssertions;
using VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Pack/unpack round-trip tests for the AV1 RTP layer. These exercise
/// <see cref="Av1RtpPacketizer"/> + <see cref="Av1RtpDepacketizer"/> in
/// isolation — no network, no jitter buffer, no encoder. The static OBU
/// stimulus mirrors what the NVENC AV1 encoder produces (Sequence Header
/// + Temporal Delimiter + Frame OBU), with payload sizes chosen to force
/// every code path: single-packet, multi-packet aggregation, fragmentation.
/// </summary>
public class Av1RtpRoundtripTests
{
    /// <summary>
    /// Build a synthetic but byte-format-correct AV1 OBU stream:
    /// Sequence Header (type 1) + Temporal Delimiter (type 2) + Frame OBU
    /// (type 6). Every OBU has obu_has_size_field=1 and a LEB128 size,
    /// matching what the encoder emits.
    /// </summary>
    private static byte[] MakeSyntheticObuStream(int framePayloadSize)
    {
        var bytes = new List<byte>();

        // Sequence Header: type=1 (header byte 0x0A = 0000 1010), size=4
        bytes.AddRange(new byte[] { 0x0A, 0x04, 0xAA, 0xBB, 0xCC, 0xDD });

        // Temporal Delimiter: type=2 (header byte 0x12), size=0
        bytes.AddRange(new byte[] { 0x12, 0x00 });

        // Frame OBU: type=6 (header byte 0x32), size=framePayloadSize
        bytes.Add(0x32);
        WriteLeb128(bytes, (uint)framePayloadSize);
        for (var i = 0; i < framePayloadSize; i++)
        {
            bytes.Add((byte)(i & 0xFF));
        }

        return bytes.ToArray();
    }

    private static void WriteLeb128(List<byte> dst, uint value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0)
            {
                b |= 0x80;
            }
            dst.Add(b);
        } while (value > 0);
    }

    [Fact]
    public void Roundtrip_small_frame_recovers_bytes_exactly()
    {
        var original = MakeSyntheticObuStream(framePayloadSize: 200);
        var packets = Av1RtpPacketizer.Packetize(original, mtu: 1200);
        packets.Count.Should().Be(1, "small frame fits in one MTU");

        var assembled = Av1RtpDepacketizer.Depacketize(packets);
        assembled.Should().NotBeNull();
        assembled!.Should().Equal(original);
    }

    [Fact]
    public void Roundtrip_large_frame_fragments_then_reassembles()
    {
        var original = MakeSyntheticObuStream(framePayloadSize: 5000);
        var packets = Av1RtpPacketizer.Packetize(original, mtu: 1200);
        packets.Count.Should().BeGreaterThan(1, "5 KB frame at MTU 1200 must fragment");

        var assembled = Av1RtpDepacketizer.Depacketize(packets);
        assembled.Should().NotBeNull();
        assembled!.Should().Equal(original);
    }

    [Fact]
    public void Roundtrip_multiple_aggregated_obus_recovers_bytes()
    {
        // 3 small OBUs (Sequence Header + Temporal Delimiter + small frame)
        // all fit in one packet — exercises W=2 aggregation path.
        var original = MakeSyntheticObuStream(framePayloadSize: 100);
        var packets = Av1RtpPacketizer.Packetize(original, mtu: 1200);
        packets.Count.Should().Be(1);

        var aggHeader = packets[0][0];
        var w = (aggHeader >> 4) & 0x3;
        w.Should().Be(2, "3 OBUs in one packet should encode as W=2");

        var assembled = Av1RtpDepacketizer.Depacketize(packets);
        assembled.Should().NotBeNull();
        assembled!.Should().Equal(original);
    }

    [Fact]
    public void Keyframe_packet_carries_N_bit()
    {
        // Sequence Header presence in the temporal unit signals keyframe
        // boundary — packetizer must set the N bit on the first packet.
        var original = MakeSyntheticObuStream(framePayloadSize: 100);
        var packets = Av1RtpPacketizer.Packetize(original, mtu: 1200);

        var aggHeader = packets[0][0];
        var nBit = (aggHeader & 0x08) != 0;
        nBit.Should().BeTrue("first packet of a keyframe must set N=1");
    }

    [Fact]
    public void Fragmented_first_packet_has_Y_set_subsequent_have_Z_set()
    {
        var original = MakeSyntheticObuStream(framePayloadSize: 5000);
        var packets = Av1RtpPacketizer.Packetize(original, mtu: 1200);

        // Find the fragmented OBU — it spans multiple packets. The frame
        // OBU is the largest one (type 6), the prior OBUs (Sequence
        // Header + Temporal Delimiter) are tiny and fit in packet 0.
        // So packet 1+ is fragments of the Frame OBU.
        var firstFragment = packets[1];
        var firstHeader = firstFragment[0];
        var z0 = (firstHeader & 0x80) != 0;
        var y0 = (firstHeader & 0x40) != 0;
        z0.Should().BeFalse("first fragment of an OBU must NOT have Z set");
        y0.Should().BeTrue("first fragment of a multi-packet OBU must have Y set");

        var lastFragment = packets[^1];
        var lastHeader = lastFragment[0];
        var zN = (lastHeader & 0x80) != 0;
        var yN = (lastHeader & 0x40) != 0;
        zN.Should().BeTrue("last fragment must have Z set");
        yN.Should().BeFalse("last fragment must NOT have Y set");
    }

    [Fact]
    public void Empty_input_returns_empty_packet_list()
    {
        Av1RtpPacketizer.Packetize(System.Array.Empty<byte>(), 1200).Should().BeEmpty();
        Av1RtpPacketizer.Packetize(null!, 1200).Should().BeEmpty();
    }

    [Fact]
    public void Empty_payload_list_returns_null_assembled_stream()
    {
        Av1RtpDepacketizer.Depacketize(System.Array.Empty<byte[]>()).Should().BeNull();
        Av1RtpDepacketizer.Depacketize(null!).Should().BeNull();
    }

    [Fact]
    public void Lossy_middle_packet_loss_does_not_throw_just_returns_garbage()
    {
        // Drop every 3rd packet — depacketizer must not crash; downstream
        // decoder will reject malformed bitstream. The contract is "don't
        // throw", not "magically reconstruct lost data".
        var original = MakeSyntheticObuStream(framePayloadSize: 5000);
        var packets = Av1RtpPacketizer.Packetize(original, mtu: 1200);
        var lossy = new List<byte[]>();
        for (var i = 0; i < packets.Count; i++)
        {
            if (i % 3 != 1)
            {
                lossy.Add(packets[i]);
            }
        }

        // Should NOT throw.
        var act = () => Av1RtpDepacketizer.Depacketize(lossy);
        act.Should().NotThrow();
    }

    [Fact]
    public void Keyframe_detection_requires_sequence_header()
    {
        // OBU stream without a Sequence Header (e.g. delta frames after
        // the keyframe): N bit must NOT be set.
        var bytes = new List<byte>
        {
            0x12, 0x00,                             // Temporal Delimiter
            0x32, 0x10,                             // Frame OBU, size=16
        };
        for (var i = 0; i < 16; i++)
        {
            bytes.Add(0x55);
        }

        var packets = Av1RtpPacketizer.Packetize(bytes.ToArray(), mtu: 1200);
        packets.Should().NotBeEmpty();
        var nBit = (packets[0][0] & 0x08) != 0;
        nBit.Should().BeFalse("delta-only temporal unit must not signal N=1");
    }
}
