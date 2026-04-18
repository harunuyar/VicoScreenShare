using FluentAssertions;
using VicoScreenShare.Client.Media.Codecs;

namespace VicoScreenShare.Tests.Client;

public class H264AccessUnitParserTests
{
    [Fact]
    public void Splits_annex_b_sample_when_it_contains_two_pictures()
    {
        var sample = BuildAnnexBSample(
            BuildNal(1, 0),
            BuildNal(1, 0));

        var accessUnits = H264AccessUnitParser.SplitIntoAnnexBAccessUnits(sample);

        accessUnits.Should().HaveCount(2);
        accessUnits[0].Should().Equal(BuildAnnexBSample(BuildNal(1, 0)));
        accessUnits[1].Should().Equal(BuildAnnexBSample(BuildNal(1, 0)));
    }

    [Fact]
    public void Keeps_multiple_slices_of_same_picture_in_one_access_unit()
    {
        var sample = BuildAnnexBSample(
            BuildNal(1, 0),
            BuildNal(1, 1));

        var accessUnits = H264AccessUnitParser.SplitIntoAnnexBAccessUnits(sample);

        accessUnits.Should().HaveCount(1);
        accessUnits[0].Should().Equal(sample);
    }

    [Fact]
    public void Converts_avcc_sample_to_annex_b_and_splits_pictures()
    {
        var sample = BuildAvccSample(
            BuildRawNal(0x67, 0x42, 0x00, 0x1E),
            BuildRawNal(0x68, 0xCE, 0x06, 0xE2),
            BuildNal(1, 0),
            BuildNal(1, 0));

        var accessUnits = H264AccessUnitParser.SplitIntoAnnexBAccessUnits(sample);

        accessUnits.Should().HaveCount(2);
        accessUnits[0][0..4].Should().Equal(new byte[] { 0, 0, 0, 1 });
        accessUnits[1][0..4].Should().Equal(new byte[] { 0, 0, 0, 1 });
        accessUnits[0].Should().Equal(BuildAnnexBSample(
            BuildRawNal(0x67, 0x42, 0x00, 0x1E),
            BuildRawNal(0x68, 0xCE, 0x06, 0xE2),
            BuildNal(1, 0)));
        accessUnits[1].Should().Equal(BuildAnnexBSample(BuildNal(1, 0)));
    }

    private static byte[] BuildNal(int nalType, params byte[] rbspBytes)
    {
        var nal = new byte[1 + rbspBytes.Length];
        nal[0] = (byte)(0x60 | (nalType & 0x1F));
        Array.Copy(rbspBytes, 0, nal, 1, rbspBytes.Length);
        return nal;
    }

    private static byte[] BuildRawNal(params byte[] rawNal) => rawNal;

    private static byte[] BuildNal(int nalType, int firstMacroblockInSlice)
    {
        return firstMacroblockInSlice switch
        {
            0 => BuildNal(nalType, (byte)0x80),
            1 => BuildNal(nalType, (byte)0x40),
            _ => throw new NotSupportedException("Test helper only supports first_mb_in_slice values 0 and 1."),
        };
    }

    private static byte[] BuildAnnexBSample(params byte[][] nalUnits)
    {
        var totalLength = nalUnits.Sum(x => 4 + x.Length);
        var sample = new byte[totalLength];
        var offset = 0;
        foreach (var nal in nalUnits)
        {
            sample[offset + 3] = 1;
            offset += 4;
            Array.Copy(nal, 0, sample, offset, nal.Length);
            offset += nal.Length;
        }

        return sample;
    }

    private static byte[] BuildAvccSample(params byte[][] nalUnits)
    {
        var totalLength = nalUnits.Sum(x => 4 + x.Length);
        var sample = new byte[totalLength];
        var offset = 0;
        foreach (var nal in nalUnits)
        {
            var length = nal.Length;
            sample[offset + 0] = (byte)((length >> 24) & 0xFF);
            sample[offset + 1] = (byte)((length >> 16) & 0xFF);
            sample[offset + 2] = (byte)((length >> 8) & 0xFF);
            sample[offset + 3] = (byte)(length & 0xFF);
            offset += 4;
            Array.Copy(nal, 0, sample, offset, nal.Length);
            offset += nal.Length;
        }

        return sample;
    }
}
