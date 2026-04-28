namespace VicoScreenShare.Client.Media.Codecs;

using System;
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
        if (rtpPayloads is null || rtpPayloads.Count == 0)
        {
            return null;
        }

        var obuElements = new List<byte[]>();
        byte[]? pendingFragment = null;

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
            var elements = new List<byte[]>();

            if (w == 0)
            {
                // Single OBU element — the rest of the payload.
                if (pos < payload.Length)
                {
                    var len = payload.Length - pos;
                    var elem = new byte[len];
                    Buffer.BlockCopy(payload, pos, elem, 0, len);
                    elements.Add(elem);
                }
            }
            else if (w <= 2)
            {
                // W=1: 2 elements (1 length-prefixed + 1 trailing).
                // W=2: 3 elements (2 length-prefixed + 1 trailing).
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
                        var elem = new byte[elemLen];
                        Buffer.BlockCopy(payload, pos, elem, 0, elemLen);
                        elements.Add(elem);
                    }
                    pos += (int)len;
                }
                // Trailing element: rest of payload, no length prefix.
                if (pos < payload.Length)
                {
                    var remaining = payload.Length - pos;
                    var elem = new byte[remaining];
                    Buffer.BlockCopy(payload, pos, elem, 0, remaining);
                    elements.Add(elem);
                }
            }
            else
            {
                // W=3: walk LEB128-prefixed elements until the remainder
                // can't fit one more (length+payload). The remainder is
                // the trailing element with no prefix.
                while (pos < payload.Length)
                {
                    var (len, leb128Bytes) = Av1RtpPacketizer.ReadLeb128(payload, pos);
                    var elemLen = (int)len;

                    if (pos + leb128Bytes + elemLen > payload.Length)
                    {
                        var remaining = payload.Length - pos;
                        var elem = new byte[remaining];
                        Buffer.BlockCopy(payload, pos, elem, 0, remaining);
                        elements.Add(elem);
                        break;
                    }

                    pos += leb128Bytes;
                    if (elemLen > 0)
                    {
                        var elem = new byte[elemLen];
                        Buffer.BlockCopy(payload, pos, elem, 0, elemLen);
                        elements.Add(elem);
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
            // continues into the next packet.
            for (var i = 0; i < elements.Count; i++)
            {
                var isFirst = (i == 0);
                var isLast = (i == elements.Count - 1);

                if (isFirst && z && pendingFragment is not null)
                {
                    var combined = new byte[pendingFragment.Length + elements[i].Length];
                    Buffer.BlockCopy(pendingFragment, 0, combined, 0, pendingFragment.Length);
                    Buffer.BlockCopy(elements[i], 0, combined, pendingFragment.Length, elements[i].Length);

                    if (isLast && y)
                    {
                        pendingFragment = combined;
                    }
                    else
                    {
                        obuElements.Add(combined);
                        pendingFragment = null;
                    }
                }
                else if (isLast && y)
                {
                    pendingFragment = elements[i];
                }
                else
                {
                    obuElements.Add(elements[i]);
                }
            }
        }

        // Dangling fragment at the end means the marker-bit packet was
        // lost. Best-effort include it; downstream decoder will reject if
        // truly broken. Without this, transient marker-bit loss would
        // discard all content silently.
        if (pendingFragment is not null)
        {
            obuElements.Add(pendingFragment);
        }

        if (obuElements.Count == 0)
        {
            return null;
        }

        return ReconstructObuStream(obuElements);
    }

    /// <summary>
    /// Convert stripped OBU elements (no size field) back into the
    /// "encoder-style" OBU stream: <c>obu_has_size_field=1</c> with a
    /// LEB128 payload size, which both the Media Foundation AV1 decoder
    /// and libdav1d expect.
    /// </summary>
    private static byte[] ReconstructObuStream(List<byte[]> elements)
    {
        var totalSize = 0;
        foreach (var elem in elements)
        {
            if (elem.Length == 0)
            {
                continue;
            }
            totalSize += 1; // OBU header
            var hasExtension = (elem[0] & 0x04) != 0;
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

        var output = new byte[totalSize];
        var pos = 0;

        foreach (var elem in elements)
        {
            if (elem.Length == 0)
            {
                continue;
            }

            var header = elem[0];
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
                output[pos++] = elem[1];
            }
            pos += Av1RtpPacketizer.WriteLeb128(output, pos, (uint)payloadLen);
            if (payloadLen > 0)
            {
                Buffer.BlockCopy(elem, headerLen, output, pos, payloadLen);
                pos += payloadLen;
            }
        }

        if (pos < output.Length)
        {
            var trimmed = new byte[pos];
            Buffer.BlockCopy(output, 0, trimmed, 0, pos);
            return trimmed;
        }
        return output;
    }
}
