using FluentAssertions;
using VicoScreenShare.Client.Media;

namespace VicoScreenShare.Tests.Client;

public class I420ToBgraTests
{
    [Fact]
    public void RequiredBgraSize_matches_row_x_column_x_4()
    {
        I420ToBgra.RequiredBgraSize(1920, 1080).Should().Be(1920 * 1080 * 4);
        I420ToBgra.RequiredBgraSize(2, 2).Should().Be(16);
    }

    [Fact]
    public void Roundtrip_solid_color_through_BgraToI420_and_back_recovers_within_tolerance()
    {
        const int width = 64;
        const int height = 64;
        var bgraIn = new byte[width * height * 4];
        for (var i = 0; i < bgraIn.Length; i += 4)
        {
            bgraIn[i + 0] = 0x20; // B
            bgraIn[i + 1] = 0x80; // G
            bgraIn[i + 2] = 0xC0; // R
            bgraIn[i + 3] = 0xFF;
        }

        var i420 = new byte[BgraToI420.RequiredOutputSize(width, height)];
        BgraToI420.Convert(bgraIn, width, height, width * 4, i420);

        var bgraOut = new byte[width * height * 4];
        I420ToBgra.Convert(i420, width, height, bgraOut, width * 4);

        // BGRA round-trip through I420 loses precision in the chroma channels.
        // A tolerance of +/- 3 on each channel is comfortable.
        for (var i = 0; i < bgraOut.Length; i += 4)
        {
            ((int)bgraOut[i + 0]).Should().BeInRange(0x1D, 0x23);
            ((int)bgraOut[i + 1]).Should().BeInRange(0x7D, 0x83);
            ((int)bgraOut[i + 2]).Should().BeInRange(0xBD, 0xC3);
            bgraOut[i + 3].Should().Be(0xFF);
        }
    }
}
