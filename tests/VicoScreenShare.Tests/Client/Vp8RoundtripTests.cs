namespace VicoScreenShare.Tests.Client;

using FluentAssertions;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using VicoScreenShare.Client.Media;

/// <summary>
/// Drives SIPSorcery's own VP8 encoder + decoder with solid-color BGRA frames
/// and inspects the decoded output to lock in the two empirical facts the
/// Phase 3 receiver relies on:
///
/// 1. <see cref="VpxVideoEncoder.EncodeVideo"/> with <see cref="VideoPixelFormatsEnum.Bgra"/>
///    mislabels channels, so the sender hand-rolls <see cref="BgraToI420"/> and
///    passes the I420 buffer instead.
/// 2. <see cref="VpxVideoEncoder.DecodeVideo"/> ignores the pixel format
///    argument and always returns a 24-bit packed BGR buffer — sample.Length
///    == width * height * 3. Treating this as I420 produces the "video cut
///    into pieces stacked on top of each other" visual. The receiver copies
///    these bytes straight into BGRA (inserting alpha) instead.
///
/// If either of these regresses, one of these tests will fail in CI and the
/// Phase 3 receive path will visibly break again.
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

        var i420 = BgraToI420.ConvertToArray(bgra, width, height, width * 4);

        byte[]? encoded = null;
        for (var attempt = 0; attempt < 10 && (encoded is null || encoded.Length == 0); attempt++)
        {
            encoded = encoder.EncodeVideo(width, height, i420, VideoPixelFormatsEnum.I420, VideoCodecsEnum.VP8);
        }
        encoded.Should().NotBeNull();
        encoded!.Length.Should().BeGreaterThan(0);

        var samples = decoder.DecodeVideo(encoded, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8);
        samples.Should().NotBeNull();

        var matched = false;
        foreach (var sample in samples!)
        {
            var w = (int)sample.Width;
            var h = (int)sample.Height;
            w.Should().Be(width);
            h.Should().Be(height);

            // Lock in the 24-bit packed layout the receiver depends on.
            sample.Sample.Should().NotBeNull();
            sample.Sample!.Length.Should().Be(w * h * 3,
                "SIPSorcery's VP8 decoder returns width*height*3 packed BGR regardless of the " +
                "VideoPixelFormatsEnum argument; the receiver relies on this exact layout");

            var cy = h / 2;
            var cx = w / 2;
            var idx = cy * w * 3 + cx * 3;

            var b = sample.Sample[idx + 0];
            var g = sample.Sample[idx + 1];
            var r = sample.Sample[idx + 2];

            if (inR == 0xFF)
            {
                r.Should().BeGreaterThan(g, $"{name}: red should dominate (B={b} G={g} R={r})");
                r.Should().BeGreaterThan(b, $"{name}: red should dominate (B={b} G={g} R={r})");
                r.Should().BeGreaterThan(50, $"{name}: red should remain visibly present (B={b} G={g} R={r})");
            }
            else if (inG == 0xFF)
            {
                g.Should().BeGreaterThan(r, $"{name}: green should dominate (B={b} G={g} R={r})");
                g.Should().BeGreaterThan(b, $"{name}: green should dominate (B={b} G={g} R={r})");
                g.Should().BeGreaterThan(50, $"{name}: green should remain visibly present (B={b} G={g} R={r})");
            }
            else if (inB == 0xFF)
            {
                b.Should().BeGreaterThan(r, $"{name}: blue should dominate (B={b} G={g} R={r})");
                b.Should().BeGreaterThan(g, $"{name}: blue should dominate (B={b} G={g} R={r})");
                b.Should().BeGreaterThan(50, $"{name}: blue should remain visibly present (B={b} G={g} R={r})");
            }
            matched = true;
        }
        matched.Should().BeTrue("decoder should have produced at least one sample we inspected");
    }
}
