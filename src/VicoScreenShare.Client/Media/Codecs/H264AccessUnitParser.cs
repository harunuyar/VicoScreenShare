namespace VicoScreenShare.Client.Media.Codecs;

using System;
using System.Collections.Generic;

/// <summary>
/// Normalizes H.264 encoder output into individual Annex B access units.
/// Media Foundation may hand back either Annex B byte streams or AVCC
/// length-prefixed samples, and a single sample can legally contain more
/// than one picture. RTP packetization must operate on access-unit
/// boundaries, not arbitrary MFT buffer boundaries.
/// </summary>
internal static class H264AccessUnitParser
{
    public static IReadOnlyList<byte[]> SplitIntoAnnexBAccessUnits(byte[] sample)
    {
        if (sample is null || sample.Length == 0)
        {
            return Array.Empty<byte[]>();
        }

        if (!TryParseNalUnits(sample, out var format, out var nalUnits) || nalUnits.Count == 0)
        {
            return new[] { sample };
        }

        var groups = GroupAccessUnits(sample, nalUnits);
        if (groups.Count == 0)
        {
            return new[] { sample };
        }

        if (groups.Count == 1 && format == H264SampleFormat.AnnexB)
        {
            return new[] { sample };
        }

        var accessUnits = new List<byte[]>(groups.Count);
        foreach (var (startIndex, endIndexExclusive) in groups)
        {
            accessUnits.Add(MaterializeAnnexBAccessUnit(sample, nalUnits, startIndex, endIndexExclusive));
        }

        return accessUnits;
    }

    private static List<(int StartIndex, int EndIndexExclusive)> GroupAccessUnits(
        byte[] sample,
        List<NalUnitRange> nalUnits)
    {
        var groups = new List<(int StartIndex, int EndIndexExclusive)>();
        var currentStart = 0;
        var currentHasVcl = false;

        for (var i = 0; i < nalUnits.Count; i++)
        {
            if (i > 0 && IsAccessUnitBoundary(sample, nalUnits, i, currentHasVcl))
            {
                groups.Add((currentStart, i));
                currentStart = i;
                currentHasVcl = false;
            }

            if (IsVclNalType(nalUnits[i].NalType))
            {
                currentHasVcl = true;
            }
        }

        groups.Add((currentStart, nalUnits.Count));
        return groups;
    }

    private static bool IsAccessUnitBoundary(
        byte[] sample,
        List<NalUnitRange> nalUnits,
        int currentIndex,
        bool currentHasVcl)
    {
        var nal = nalUnits[currentIndex];

        if (nal.NalType == 9)
        {
            return true;
        }

        if (!currentHasVcl)
        {
            return false;
        }

        if (IsPrefixNalType(nal.NalType))
        {
            return true;
        }

        if (!IsVclNalType(nal.NalType))
        {
            return false;
        }

        return StartsNewPicture(sample.AsSpan(nal.PayloadStart, nal.PayloadLength));
    }

    private static byte[] MaterializeAnnexBAccessUnit(
        byte[] sample,
        List<NalUnitRange> nalUnits,
        int startIndex,
        int endIndexExclusive)
    {
        var totalLength = 0;
        for (var i = startIndex; i < endIndexExclusive; i++)
        {
            totalLength += 4 + nalUnits[i].PayloadLength;
        }

        var output = new byte[totalLength];
        var offset = 0;
        for (var i = startIndex; i < endIndexExclusive; i++)
        {
            output[offset + 0] = 0;
            output[offset + 1] = 0;
            output[offset + 2] = 0;
            output[offset + 3] = 1;
            offset += 4;

            Buffer.BlockCopy(sample, nalUnits[i].PayloadStart, output, offset, nalUnits[i].PayloadLength);
            offset += nalUnits[i].PayloadLength;
        }

        return output;
    }

    private static bool TryParseNalUnits(
        byte[] sample,
        out H264SampleFormat format,
        out List<NalUnitRange> nalUnits)
    {
        if (TryParseAnnexB(sample, out nalUnits))
        {
            format = H264SampleFormat.AnnexB;
            return true;
        }

        if (TryParseAvcc(sample, out nalUnits))
        {
            format = H264SampleFormat.Avcc;
            return true;
        }

        format = H264SampleFormat.Unknown;
        nalUnits = new List<NalUnitRange>();
        return false;
    }

    private static bool TryParseAnnexB(byte[] sample, out List<NalUnitRange> nalUnits)
    {
        nalUnits = new List<NalUnitRange>();
        var firstStart = FindStartCode(sample, 0, out var firstPrefixLength);
        if (firstStart < 0)
        {
            return false;
        }

        var currentPrefixStart = firstStart;
        var currentPayloadStart = firstStart + firstPrefixLength;
        while (currentPayloadStart < sample.Length)
        {
            var nextPrefixStart = FindStartCode(sample, currentPayloadStart, out var nextPrefixLength);
            var payloadEnd = nextPrefixStart >= 0 ? nextPrefixStart : sample.Length;
            if (payloadEnd > currentPayloadStart)
            {
                nalUnits.Add(new NalUnitRange(
                    PayloadStart: currentPayloadStart,
                    PayloadLength: payloadEnd - currentPayloadStart,
                    NalType: sample[currentPayloadStart] & 0x1F));
            }

            if (nextPrefixStart < 0)
            {
                break;
            }

            currentPrefixStart = nextPrefixStart;
            currentPayloadStart = currentPrefixStart + nextPrefixLength;
        }

        return nalUnits.Count > 0;
    }

    private static bool TryParseAvcc(byte[] sample, out List<NalUnitRange> nalUnits)
    {
        nalUnits = new List<NalUnitRange>();
        var offset = 0;
        while (offset + 4 <= sample.Length)
        {
            var nalLength =
                (sample[offset] << 24)
                | (sample[offset + 1] << 16)
                | (sample[offset + 2] << 8)
                | sample[offset + 3];
            offset += 4;

            if (nalLength <= 0 || offset + nalLength > sample.Length)
            {
                nalUnits.Clear();
                return false;
            }

            nalUnits.Add(new NalUnitRange(
                PayloadStart: offset,
                PayloadLength: nalLength,
                NalType: sample[offset] & 0x1F));
            offset += nalLength;
        }

        if (offset != sample.Length)
        {
            nalUnits.Clear();
            return false;
        }

        return nalUnits.Count > 0;
    }

    private static int FindStartCode(byte[] sample, int offset, out int prefixLength)
    {
        for (var i = offset; i <= sample.Length - 3; i++)
        {
            if (sample[i] != 0 || sample[i + 1] != 0)
            {
                continue;
            }

            if (sample[i + 2] == 1)
            {
                prefixLength = 3;
                return i;
            }

            if (i + 3 < sample.Length && sample[i + 2] == 0 && sample[i + 3] == 1)
            {
                prefixLength = 4;
                return i;
            }
        }

        prefixLength = 0;
        return -1;
    }

    private static bool StartsNewPicture(ReadOnlySpan<byte> nalPayload)
    {
        if (nalPayload.Length <= 1)
        {
            return false;
        }

        var rbsp = RemoveEmulationPreventionBytes(nalPayload.Slice(1));
        if (rbsp.Length == 0)
        {
            return false;
        }

        var reader = new BitReader(rbsp);
        return reader.TryReadUnsignedExpGolomb(out var firstMacroblockInSlice) && firstMacroblockInSlice == 0;
    }

    private static byte[] RemoveEmulationPreventionBytes(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        var written = 0;
        var zeroRun = 0;

        for (var i = 0; i < data.Length; i++)
        {
            var value = data[i];
            if (zeroRun >= 2 && value == 0x03)
            {
                zeroRun = 0;
                continue;
            }

            output[written++] = value;
            zeroRun = value == 0 ? zeroRun + 1 : 0;
        }

        if (written == output.Length)
        {
            return output;
        }

        Array.Resize(ref output, written);
        return output;
    }

    private static bool IsVclNalType(int nalType) => nalType is >= 1 and <= 5;

    private static bool IsPrefixNalType(int nalType) => nalType is 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18;

    private readonly record struct NalUnitRange(int PayloadStart, int PayloadLength, int NalType);

    private enum H264SampleFormat
    {
        Unknown = 0,
        AnnexB = 1,
        Avcc = 2,
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bitOffset;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bitOffset = 0;
        }

        public bool TryReadUnsignedExpGolomb(out int value)
        {
            value = 0;

            var leadingZeroCount = 0;
            while (true)
            {
                if (!TryReadBit(out var bit))
                {
                    return false;
                }

                if (bit)
                {
                    break;
                }

                leadingZeroCount++;
                if (leadingZeroCount > 31)
                {
                    return false;
                }
            }

            if (leadingZeroCount == 0)
            {
                value = 0;
                return true;
            }

            var suffix = 0;
            for (var i = 0; i < leadingZeroCount; i++)
            {
                if (!TryReadBit(out var bit))
                {
                    return false;
                }

                suffix = (suffix << 1) | (bit ? 1 : 0);
            }

            value = ((1 << leadingZeroCount) - 1) + suffix;
            return true;
        }

        private bool TryReadBit(out bool value)
        {
            if (_bitOffset >= _data.Length * 8)
            {
                value = false;
                return false;
            }

            var byteIndex = _bitOffset / 8;
            var bitIndex = 7 - (_bitOffset % 8);
            value = ((_data[byteIndex] >> bitIndex) & 1) != 0;
            _bitOffset++;
            return true;
        }
    }
}
