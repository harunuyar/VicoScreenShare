using FluentAssertions;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace ScreenSharing.Tests.Client;

/// <summary>
/// Drives SIPSorcery's own VP8 encoder + decoder with known solid-color BGRA
/// frames and inspects the decoded output. Locks in the RGB/BGR byte-order
/// interpretation we established empirically: <c>PixelConverter.I420toBGR</c>
/// actually lays bytes out in R, G, B order despite the name, so the receiver
/// must read the returned buffer as RGB and swap into BGRA.
///
/// VP8 lossy round-trip introduces significant chroma drift even on solid
/// colors at low quality presets; the assertions are loose ("the right channel
/// is the dominant one") rather than pixel-accurate.
/// </summary>
public class Vp8RoundtripTests
{
    [Theory]
    [InlineData(0x00, 0x00, 0xFF, "red")]
    [InlineData(0xFF, 0x00, 0x00, "blue")]
    [InlineData(0x00, 0xFF, 0x00, "green")]
    public void Dominant_channel_survives_vp8_roundtrip(byte inB, byte inG, byte inR, string name)
    {
        const int width = 128;
        const int height = 128;

        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = inB;
            bgra[i + 1] = inG;
            bgra[i + 2] = inR;
            bgra[i + 3] = 0xFF;
        }

        using var encoder = new VpxVideoEncoder();
        using var decoder = new VpxVideoEncoder();

        var i420 = ScreenSharing.Client.Media.BgraToI420.ConvertToArray(bgra, width, height, width * 4);

        byte[]? encoded = null;
        for (var attempt = 0; attempt < 10 && (encoded is null || encoded.Length == 0); attempt++)
        {
            encoded = encoder.EncodeVideo(width, height, i420, VideoPixelFormatsEnum.I420, VideoCodecsEnum.VP8);
        }
        encoded.Should().NotBeNull();
        encoded!.Length.Should().BeGreaterThan(0);

        var samples = decoder.DecodeVideo(encoded, VideoPixelFormatsEnum.I420, VideoCodecsEnum.VP8);
        samples.Should().NotBeNull();

        var matched = false;
        foreach (var sample in samples!)
        {
            var w = (int)sample.Width;
            var h = (int)sample.Height;

            // PixelConverter.I420toBGR is misnamed: it lays bytes out as R,G,B.
            // Mirror the StreamReceiver's fix: read the result as RGB.
            var rgb = PixelConverter.I420toBGR(sample.Sample!, w, h, out var stride);
            var cy = h / 2;
            var cx = w / 2;
            var idx = cy * stride + cx * 3;
            var r = rgb[idx + 0];
            var g = rgb[idx + 1];
            var b = rgb[idx + 2];

            // The dominant channel of the input must be the dominant channel of
            // the output. VP8 at low preset chops a LOT of magnitude off solid
            // colors (observed ~113 for 255 green, for example), so we just
            // check that the intended channel is the max and is still visibly
            // present.
            if (inR == 0xFF)
            {
                r.Should().BeGreaterThan(g, $"{name}: red should dominate");
                r.Should().BeGreaterThan(b, $"{name}: red should dominate");
                r.Should().BeGreaterThan(50, $"{name}: red should remain visibly present");
            }
            else if (inG == 0xFF)
            {
                g.Should().BeGreaterThan(r, $"{name}: green should dominate");
                g.Should().BeGreaterThan(b, $"{name}: green should dominate");
                g.Should().BeGreaterThan(50, $"{name}: green should remain visibly present");
            }
            else if (inB == 0xFF)
            {
                b.Should().BeGreaterThan(r, $"{name}: blue should dominate");
                b.Should().BeGreaterThan(g, $"{name}: blue should dominate");
                b.Should().BeGreaterThan(50, $"{name}: blue should remain visibly present");
            }
            matched = true;
        }
        matched.Should().BeTrue("decoder should have produced at least one sample we inspected");
    }
}
