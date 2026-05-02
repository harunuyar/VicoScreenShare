namespace VicoScreenShare.Client.Media.Codecs;

using System;
using System.Buffers;
using System.Collections.Generic;

/// <summary>
/// Reassembles AV1 OBU temporal units from RTP payloads, per RFC 9066.
/// Caller hands every payload for one frame (same RTP timestamp, marker bit
/// on the last) and gets back the OBU stream the decoder expects:
/// each OBU has <c>obu_has_size_field = 1</c> and a LEB128 payload size.
///
/// The wire form has the size bit cleared (the LEB128 length lives in the
/// RFC 9066 aggregation), so we re-add it here. Mirrors
/// <see cref="Av1RtpPacketizer"/>.
/// </summary>
public static class Av1RtpDepacketizer
{
    /// <summary>
    /// Reassemble RTP payloads into a complete AV1 OBU stream for the
    /// decoder. Returns null when the input is empty or every payload
    /// turned out to be malformed. Dangling fragments at the tail are
    /// included on a best-effort basis — the decoder will reject what it
    /// can't parse rather than crash, and the depacketizer's job is to
    /// preserve information rather than judge it.
    /// </summary>
    public static byte[]? Depacketize(IReadOnlyList<byte[]> rtpPayloads)
    {
        var obuElements = BuildObuElements(rtpPayloads);
        if (obuElements is null || obuElements.Count == 0)
        {
            ReturnPooledElements(obuElements);
            return null;
        }
        try
        {
            return ReconstructObuStream(obuElements);
        }
        finally
        {
            ReturnPooledElements(obuElements);
        }
    }

    /// <summary>
    /// One OBU element extracted from RTP payloads. <see cref="Buffer"/>
    /// is rented from <see cref="ArrayPool{T}.Shared"/>; <see cref="Length"/>
    /// is the valid byte count. Caller must call
    /// <see cref="ReturnPooledElements"/> after consuming the elements
    /// or the pool will leak.
    /// </summary>
    private readonly record struct ObuElement(byte[] Buffer, int Length);

    private static void ReturnPooledElements(List<ObuElement>? elements)
    {
        if (elements is null)
        {
            return;
        }
        for (var i = 0; i < elements.Count; i++)
        {
            var buf = elements[i].Buffer;
            if (buf is not null)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
        elements.Clear();
    }

    private static ObuElement RentElement(byte[] source, int sourceOffset, int length)
    {
        var buf = ArrayPool<byte>.Shared.Rent(length);
        if (length > 0)
        {
            Buffer.BlockCopy(source, sourceOffset, buf, 0, length);
        }
        return new ObuElement(buf, length);
    }

    /// <summary>
    /// Walks RTP payloads applying the RFC 9066 aggregation header (Z/Y
    /// fragmentation, W element count) and returns a flat list of
    /// stripped OBU element buffers (header + ext + payload, no
    /// obu_has_size_field, no LEB128 size). All buffers are rented from
    /// <see cref="ArrayPool{T}.Shared"/>; caller MUST return them via
    /// <see cref="ReturnPooledElements"/>. Shared by both
    /// <see cref="Depacketize"/> and <see cref="DepacketizePooled"/>.
    /// </summary>
    private static List<ObuElement>? BuildObuElements(IReadOnlyList<byte[]> rtpPayloads)
    {
        if (rtpPayloads is null || rtpPayloads.Count == 0)
        {
            return null;
        }

        var obuElements = new List<ObuElement>();
        ObuElement pendingFragment = default;
        var hasPendingFragment = false;

        for (var pktIdx = 0; pktIdx < rtpPayloads.Count; pktIdx++)
        {
            var payload = rtpPayloads[pktIdx];
            if (payload is null || payload.Length < 1)
            {
                continue;
            }

            var aggHeader = payload[0];
            var z = (aggHeader & 0x80) != 0;
            var y = (aggHeader & 0x40) != 0;
            var w = (aggHeader >> 4) & 0x3;

            var pos = 1;
            var packetElements = new List<ObuElement>(4);

            if (w == 0)
            {
                if (pos < payload.Length)
                {
                    var len = payload.Length - pos;
                    packetElements.Add(RentElement(payload, pos, len));
                }
            }
            else if (w <= 2)
            {
                for (var i = 0; i < w; i++)
                {
                    if (pos >= payload.Length)
                    {
                        break;
                    }
                    var (len, leb128Bytes) = Av1RtpPacketizer.ReadLeb128(payload, pos);
                    pos += leb128Bytes;
                    var elemLen = (int)len;
                    if (pos + elemLen > payload.Length)
                    {
                        elemLen = payload.Length - pos;
                    }
                    if (elemLen > 0)
                    {
                        packetElements.Add(RentElement(payload, pos, elemLen));
                    }
                    pos += (int)len;
                }
                if (pos < payload.Length)
                {
                    var remaining = payload.Length - pos;
                    packetElements.Add(RentElement(payload, pos, remaining));
                }
            }
            else
            {
                while (pos < payload.Length)
                {
                    var (len, leb128Bytes) = Av1RtpPacketizer.ReadLeb128(payload, pos);
                    var elemLen = (int)len;
                    if (pos + leb128Bytes + elemLen > payload.Length)
                    {
                        var remaining = payload.Length - pos;
                        packetElements.Add(RentElement(payload, pos, remaining));
                        break;
                    }
                    pos += leb128Bytes;
                    if (elemLen > 0)
                    {
                        packetElements.Add(RentElement(payload, pos, elemLen));
                    }
                    pos += elemLen;
                    if (pos >= payload.Length)
                    {
                        break;
                    }
                }
            }

            // Apply Z/Y fragmentation: Z=1 first element continues a
            // fragment from the previous packet, Y=1 last element
            // continues into the next packet. Combining two pooled
            // buffers rents a third (whose size = sum), copies into it,
            // and returns the two source buffers to the pool.
            for (var i = 0; i < packetElements.Count; i++)
            {
                var elem = packetElements[i];
                var isFirst = (i == 0);
                var isLast = (i == packetElements.Count - 1);

                if (isFirst && z && hasPendingFragment)
                {
                    var combinedLen = pendingFragment.Length + elem.Length;
                    var combinedBuf = ArrayPool<byte>.Shared.Rent(combinedLen);
                    if (pendingFragment.Length > 0)
                    {
                        Buffer.BlockCopy(pendingFragment.Buffer, 0, combinedBuf, 0, pendingFragment.Length);
                    }
                    if (elem.Length > 0)
                    {
                        Buffer.BlockCopy(elem.Buffer, 0, combinedBuf, pendingFragment.Length, elem.Length);
                    }
                    ArrayPool<byte>.Shared.Return(pendingFragment.Buffer);
                    ArrayPool<byte>.Shared.Return(elem.Buffer);
                    var combined = new ObuElement(combinedBuf, combinedLen);

                    if (isLast && y)
                    {
                        pendingFragment = combined;
                    }
                    else
                    {
                        obuElements.Add(combined);
                        pendingFragment = default;
                        hasPendingFragment = false;
                    }
                }
                else if (isLast && y)
                {
                    pendingFragment = elem;
                    hasPendingFragment = true;
                }
                else
                {
                    obuElements.Add(elem);
                }
            }
        }

        // Dangling fragment at the end means the marker-bit packet was
        // lost. Best-effort include it; downstream decoder will reject
        // if truly broken.
        if (hasPendingFragment)
        {
            obuElements.Add(pendingFragment);
        }

        return obuElements;
    }

    /// <summary>
    /// Pooled-buffer variant of <see cref="Depacketize"/>. Returns the
    /// assembled OBU stream in a buffer rented from
    /// <see cref="ArrayPool{T}.Shared"/>; the caller MUST return the
    /// rented buffer to the pool when finished. Avoids the LOH
    /// allocation that <see cref="Depacketize"/> incurs on every IDR
    /// (assembled stream &gt;= 85 KB lands on LOH and gets promoted to
    /// Gen2; at 1 IDR/sec on screen content this runs Gen2 collections
    /// that pause the decoder thread for tens of ms each, false-
    /// triggering the wedged-decoder watchdog).
    /// </summary>
    public static (byte[]? PooledBuffer, int Length) DepacketizePooled(IReadOnlyList<byte[]> rtpPayloads)
    {
        var obuElements = BuildObuElements(rtpPayloads);
        if (obuElements is null || obuElements.Count == 0)
        {
            ReturnPooledElements(obuElements);
            return (null, 0);
        }
        try
        {
            return ReconstructObuStreamPooled(obuElements);
        }
        finally
        {
            ReturnPooledElements(obuElements);
        }
    }

    /// <summary>
    /// Pooled variant of <see cref="ReconstructObuStream"/>. Computes
    /// the exact output size, rents a buffer of at least that size from
    /// <see cref="ArrayPool{T}.Shared"/>, and writes the reconstructed
    /// stream into the rented buffer. Caller is responsible for
    /// returning the buffer to the pool.
    /// </summary>
    private static (byte[] PooledBuffer, int Length) ReconstructObuStreamPooled(List<ObuElement> elements)
    {
        var totalSize = ComputeReconstructedSize(elements);
        var pooled = ArrayPool<byte>.Shared.Rent(totalSize);
        var written = WriteReconstructedObuStream(elements, pooled);
        return (pooled, written);
    }

    /// <summary>
    /// Convert stripped OBU elements (no size field) back into the
    /// "encoder-style" OBU stream: <c>obu_has_size_field=1</c> with a
    /// LEB128 payload size, which both the Media Foundation AV1 decoder
    /// and libdav1d expect.
    /// </summary>
    private static byte[] ReconstructObuStream(List<ObuElement> elements)
    {
        var totalSize = ComputeReconstructedSize(elements);
        var output = new byte[totalSize];
        var written = WriteReconstructedObuStream(elements, output);
        if (written < output.Length)
        {
            var trimmed = new byte[written];
            Buffer.BlockCopy(output, 0, trimmed, 0, written);
            return trimmed;
        }
        return output;
    }

    private static int ComputeReconstructedSize(List<ObuElement> elements)
    {
        var totalSize = 0;
        foreach (var elem in elements)
        {
            if (elem.Length == 0)
            {
                continue;
            }
            totalSize += 1; // OBU header
            var hasExtension = (elem.Buffer[0] & 0x04) != 0;
            if (hasExtension)
            {
                totalSize += 1;
            }
            var payloadLen = elem.Length - 1 - (hasExtension ? 1 : 0);
            if (payloadLen < 0)
            {
                payloadLen = 0;
            }
            totalSize += Av1RtpPacketizer.Leb128Size(payloadLen);
            totalSize += payloadLen;
        }
        return totalSize;
    }

    private static int WriteReconstructedObuStream(List<ObuElement> elements, byte[] output)
    {
        var pos = 0;
        foreach (var elem in elements)
        {
            if (elem.Length == 0)
            {
                continue;
            }
            var header = elem.Buffer[0];
            var hasExtension = (header & 0x04) != 0;
            var headerLen = 1 + (hasExtension ? 1 : 0);
            var payloadLen = elem.Length - headerLen;
            if (payloadLen < 0)
            {
                payloadLen = 0;
            }
            output[pos++] = (byte)(header | 0x02); // set obu_has_size_field
            if (hasExtension && elem.Length >= 2)
            {
                output[pos++] = elem.Buffer[1];
            }
            pos += Av1RtpPacketizer.WriteLeb128(output, pos, (uint)payloadLen);
            if (payloadLen > 0)
            {
                Buffer.BlockCopy(elem.Buffer, headerLen, output, pos, payloadLen);
                pos += payloadLen;
            }
        }
        return pos;
    }
}
