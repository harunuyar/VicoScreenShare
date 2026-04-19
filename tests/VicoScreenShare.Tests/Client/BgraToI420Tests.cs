namespace VicoScreenShare.Tests.Client;

using FluentAssertions;
using VicoScreenShare.Client.Media;

public class BgraToI420Tests
{
    [Fact]
    public void RequiredOutputSize_matches_I420_layout()
    {
        BgraToI420.RequiredOutputSize(4, 4).Should().Be(4 * 4 + 2 * 2 + 2 * 2);   // 16 + 4 + 4
        BgraToI420.RequiredOutputSize(1920, 1080).Should().Be(1920 * 1080 * 3 / 2);
    }

    [Fact]
    public void Convert_produces_expected_luma_for_solid_white()
    {
        // A 2x2 solid-white BGRA source: each pixel is FF FF FF FF (B G R A).
        var src = new byte[2 * 2 * 4];
        for (var i = 0; i < src.Length; i++)
        {
            src[i] = 0xFF;
        }

        var dst = new byte[BgraToI420.RequiredOutputSize(2, 2)];

        BgraToI420.Convert(src, 2, 2, bgraStrideBytes: 8, dst);

        // Y plane for full white should be ~255 on every pixel.
        dst[0].Should().BeGreaterThanOrEqualTo(250);
        dst[1].Should().BeGreaterThanOrEqualTo(250);
        dst[2].Should().BeGreaterThanOrEqualTo(250);
        dst[3].Should().BeGreaterThanOrEqualTo(250);

        // U/V should be ~128 (no chroma) for full white.
        dst[4].Should().BeInRange(120, 136);
        dst[5].Should().BeInRange(120, 136);
    }

    [Fact]
    public void Convert_produces_expected_luma_for_solid_black()
    {
        var src = new byte[2 * 2 * 4];
        // BGRA = 00 00 00 FF — black with full alpha
        for (var i = 3; i < src.Length; i += 4)
        {
            src[i] = 0xFF;
        }

        var dst = new byte[BgraToI420.RequiredOutputSize(2, 2)];

        BgraToI420.Convert(src, 2, 2, 8, dst);

        dst[0].Should().Be(0);
        dst[1].Should().Be(0);
        dst[2].Should().Be(0);
        dst[3].Should().Be(0);
        dst[4].Should().BeInRange(120, 136);
        dst[5].Should().BeInRange(120, 136);
    }

    [Fact]
    public void Convert_handles_non_packed_stride()
    {
        // Same 2x2 red frame, but the source stride adds 8 bytes of padding per row.
        const int width = 2;
        const int height = 2;
        const int stride = width * 4 + 8;
        var src = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = y * stride + x * 4;
                src[p + 0] = 0x00; // B
                src[p + 1] = 0x00; // G
                src[p + 2] = 0xFF; // R
                src[p + 3] = 0xFF; // A
            }
        }

        var dst = new byte[BgraToI420.RequiredOutputSize(width, height)];
        BgraToI420.Convert(src, width, height, stride, dst);

        // Red under BT.601 full range is Y ~= 76, U ~= 85, V ~= 255.
        dst[0].Should().BeInRange(70, 85);
        dst[4].Should().BeInRange(75, 95);  // U
        dst[5].Should().BeInRange(245, 255); // V
    }
}
