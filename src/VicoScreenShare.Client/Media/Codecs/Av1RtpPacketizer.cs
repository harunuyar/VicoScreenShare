namespace VicoScreenShare.Client.Media.Codecs;

using System;
using System.Collections.Generic;

/// <summary>
/// Packetizes one AV1 temporal unit (the OBU stream produced by the encoder
/// for one frame) into RTP payloads, per RFC 9066 (AV1 RTP payload format).
///
/// Aggregation header is one byte at the start of each RTP payload:
/// <code>
///   bits:  7  6  5  4  3  2 1 0
///         | Z| Y|  W  | N|  reserved (3) |
/// </code>
/// <list type="bullet">
///   <item>Z = 1: this packet's first OBU element continues a fragment from the previous packet.</item>
///   <item>Y = 1: this packet's last OBU element continues into the next packet.</item>
///   <item>W (2 bits): 0 = single OBU, 1..3 = number of length-prefixed OBU elements before the trailing one.</item>
///   <item>N = 1: first OBU is a Sequence Header that starts a new coded video sequence (keyframe boundary).</item>
/// </list>
///
/// The encoder produces OBUs with <c>obu_has_size_field = 1</c> and a LEB128
/// payload length. The wire format inside a packet aggregates OBU elements
/// with <c>obu_has_size_field</c> CLEARED — the LEB128 length lives in the
/// aggregation, not the OBU header itself. <see cref="Av1RtpDepacketizer"/>
/// re-adds the size field on reassembly because the Media Foundation /
/// libdav1d decoders want OBUs with sizes.
/// </summary>
public static class Av1RtpPacketizer
{
    private const int AggregationHeaderSize = 1;
    private const int ObuTypeSequenceHeader = 1;
    private const int ObuTypeTemporalDelimiter = 2;

    /// <summary>
    /// Packetize an encoder OBU temporal unit into RTP payloads, each at
    /// most <paramref name="mtu"/> bytes. The caller is responsible for
    /// setting the RTP marker bit on the last payload of the frame.
    /// </summary>
    public static IReadOnlyList<byte[]> Packetize(byte[] obuStream, int mtu = 1200)
    {
        if (obuStream is null || obuStream.Length == 0)
        {
            return Array.Empty<byte[]>();
        }

        var obus = ParseObus(obuStream);
        if (obus.Count == 0)
        {
            return Array.Empty<byte[]>();
        }

        // The N (new coded video sequence) bit goes on the first packet of
        // a temporal unit that starts with a Sequence Header — i.e. a
        // keyframe. Receiver uses this to know when it can start decoding.
        var isKeyframe = false;
        foreach (var obu in obus)
        {
            if (obu.Type == ObuTypeSequenceHeader)
            {
                isKeyframe = true;
                break;
            }
        }

        var maxPayload = mtu - AggregationHeaderSize;
        if (maxPayload <= 0)
        {
            maxPayload = 1;
        }

        var packets = new List<byte[]>();
        var pending = new List<byte[]>();
        var pendingSize = 0;

        for (var i = 0; i < obus.Count; i++)
        {
            var elemBytes = obus[i].StrippedBytes;

            if (pending.Count == 0)
            {
                if (elemBytes.Length <= maxPayload)
                {
                    pending.Add(elemBytes);
                    pendingSize = elemBytes.Length;
                    continue;
                }
                FragmentObu(packets, elemBytes, maxPayload,
                    n: isKeyframe && packets.Count == 0);
                continue;
            }

            // Adding another element means the previous-last element gains
            // a LEB128 length prefix in the aggregation. Account for that.
            var prevLastLen = pending[pending.Count - 1].Length;
            var leb128Cost = Leb128Size(prevLastLen);
            var needed = leb128Cost + elemBytes.Length;

            if (pendingSize + needed <= maxPayload)
            {
                pending.Add(elemBytes);
                pendingSize += needed;
            }
            else
            {
                FlushAggregation(packets, pending,
                    z: false, y: false,
                    n: isKeyframe && packets.Count == 0);
                pending.Clear();
                pendingSize = 0;

                if (elemBytes.Length <= maxPayload)
                {
                    pending.Add(elemBytes);
                    pendingSize = elemBytes.Length;
                }
                else
                {
                    FragmentObu(packets, elemBytes, maxPayload,
                        n: isKeyframe && packets.Count == 0);
                }
            }
        }

        if (pending.Count > 0)
        {
            FlushAggregation(packets, pending,
                z: false, y: false,
                n: isKeyframe && packets.Count == 0);
        }

        return packets;
    }

    /// <summary>
    /// Fragment a single OBU element across multiple RTP packets. First
    /// fragment Z=0 Y=1, middle fragments Z=1 Y=1, last fragment Z=1 Y=0.
    /// All fragments use W=0 (single element, length inferred from packet
    /// boundaries).
    /// </summary>
    private static void FragmentObu(List<byte[]> packets, byte[] elemBytes, int maxPayload, bool n)
    {
        var offset = 0;
        var first = true;

        while (offset < elemBytes.Length)
        {
            var chunkSize = Math.Min(maxPayload, elemBytes.Length - offset);
            var last = (offset + chunkSize >= elemBytes.Length);

            var aggHeader = BuildAggregationHeader(
                z: !first,
                y: !last,
                w: 0,
                n: n && first);

            var packet = new byte[AggregationHeaderSize + chunkSize];
            packet[0] = aggHeader;
            Buffer.BlockCopy(elemBytes, offset, packet, AggregationHeaderSize, chunkSize);
            packets.Add(packet);

            offset += chunkSize;
            first = false;
        }
    }

    /// <summary>
    /// Pack one or more whole OBU elements into a single aggregation packet.
    /// </summary>
    private static void FlushAggregation(List<byte[]> packets, List<byte[]> elements, bool z, bool y, bool n)
    {
        if (elements.Count == 0)
        {
            return;
        }

        // W field meaning:
        //   0  = single element, no length prefixes
        //   1  = 2 elements, first has LEB128 length prefix
        //   2  = 3 elements, first two have prefixes
        //   3  = N elements, first N-1 have prefixes (count discoverable
        //        only by walking the packet — used when we have 4+).
        int w;
        if (elements.Count == 1)
        {
            w = 0;
        }
        else if (elements.Count <= 3)
        {
            w = elements.Count - 1;
        }
        else
        {
            w = 3;
        }

        var aggHeader = BuildAggregationHeader(z, y, w, n);

        var totalSize = AggregationHeaderSize;
        for (var i = 0; i < elements.Count; i++)
        {
            var needsLength = (elements.Count > 1) && (i < elements.Count - 1);
            if (needsLength)
            {
                totalSize += Leb128Size(elements[i].Length);
            }
            totalSize += elements[i].Length;
        }

        var packet = new byte[totalSize];
        packet[0] = aggHeader;
        var pos = AggregationHeaderSize;

        for (var i = 0; i < elements.Count; i++)
        {
            var needsLength = (elements.Count > 1) && (i < elements.Count - 1);
            if (needsLength)
            {
                pos += WriteLeb128(packet, pos, (uint)elements[i].Length);
            }
            Buffer.BlockCopy(elements[i], 0, packet, pos, elements[i].Length);
            pos += elements[i].Length;
        }

        packets.Add(packet);
    }

    private static byte BuildAggregationHeader(bool z, bool y, int w, bool n)
    {
        var header = 0;
        if (z)
        {
            header |= 0x80;
        }
        if (y)
        {
            header |= 0x40;
        }
        header |= (w & 0x3) << 4;
        if (n)
        {
            header |= 0x08;
        }
        return (byte)header;
    }

    private struct Obu
    {
        public int Type;
        /// <summary>OBU element bytes with obu_has_size_field cleared
        /// (header + ext + payload, no LEB128 size). This is the form RFC
        /// 9066 wants on the wire — the LEB128 size lives in the
        /// aggregation, not in the OBU header.</summary>
        public byte[] StrippedBytes;
    }

    /// <summary>
    /// Parse the encoder's OBU stream (each OBU has obu_has_size_field=1 +
    /// LEB128 size) into a flat list of stripped OBU elements (size bit
    /// cleared, LEB128 size removed).
    /// </summary>
    private static List<Obu> ParseObus(byte[] stream)
    {
        var result = new List<Obu>();
        var offset = 0;

        while (offset < stream.Length)
        {
            if (offset + 1 > stream.Length)
            {
                break;
            }
            var header = stream[offset];
            var obuType = (header >> 3) & 0xF;
            var hasExtension = (header & 0x04) != 0;
            var hasSize = (header & 0x02) != 0;

            var headerLen = 1 + (hasExtension ? 1 : 0);
            var dataStart = offset + headerLen;

            int payloadSize;
            if (hasSize)
            {
                var (size, leb128Bytes) = ReadLeb128(stream, dataStart);
                dataStart += leb128Bytes;
                payloadSize = (int)size;
            }
            else
            {
                payloadSize = stream.Length - dataStart;
            }

            var obuEnd = dataStart + payloadSize;
            if (obuEnd > stream.Length)
            {
                obuEnd = stream.Length;
            }
            payloadSize = obuEnd - dataStart;

            // Strip the size bit so the wire form matches RFC 9066. We
            // KEEP Temporal Delimiter OBUs in the stream — RFC 9066 says
            // they're optional but the MF AV1 decoder benefits from
            // seeing them as temporal-unit boundaries. Sender side keeps
            // them; receiver side has the option to strip if needed.
            var strippedHeader = (byte)(header & 0xFD);
            var elemLen = 1 + (hasExtension ? 1 : 0) + payloadSize;
            var elemBytes = new byte[elemLen];
            elemBytes[0] = strippedHeader;
            var pos = 1;
            if (hasExtension)
            {
                elemBytes[pos++] = stream[offset + 1];
            }
            if (payloadSize > 0)
            {
                Buffer.BlockCopy(stream, dataStart, elemBytes, pos, payloadSize);
            }

            result.Add(new Obu { Type = obuType, StrippedBytes = elemBytes });

            offset = obuEnd;
        }

        return result;
    }

    // --- LEB128 helpers (shared with depacketizer) ---

    internal static (uint value, int bytesRead) ReadLeb128(byte[] data, int offset)
    {
        uint value = 0;
        var i = 0;
        while (i < 8 && offset + i < data.Length)
        {
            var b = data[offset + i];
            value |= (uint)(b & 0x7F) << (i * 7);
            i++;
            if ((b & 0x80) == 0)
            {
                break;
            }
        }
        return (value, i);
    }

    internal static int WriteLeb128(byte[] buffer, int offset, uint value)
    {
        var written = 0;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0)
            {
                b |= 0x80;
            }
            buffer[offset + written] = b;
            written++;
        } while (value > 0);
        return written;
    }

    internal static int Leb128Size(int value)
    {
        if (value < 0)
        {
            return 1;
        }
        var v = (uint)value;
        var size = 0;
        do
        {
            v >>= 7;
            size++;
        } while (v > 0);
        return size;
    }
}
