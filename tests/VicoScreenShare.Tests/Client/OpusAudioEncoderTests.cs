namespace VicoScreenShare.Tests.Client;

using System;
using FluentAssertions;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Round-trip Opus encode → decode on a known 440 Hz sine wave and check
/// that the dominant frequency survives and sample count / duration
/// arithmetic is self-consistent. If either half of the Concentus wrapper
/// regresses (wrong channel count, wrong frame length, bitrate ignored),
/// the assertions here fire.
/// </summary>
public class OpusAudioEncoderTests
{
    [Theory]
    [InlineData(true, 96_000)]
    [InlineData(false, 64_000)]
    public void Sine_survives_opus_roundtrip(bool stereo, int bitrate)
    {
        var settings = new AudioSettings
        {
            Stereo = stereo,
            TargetBitrate = bitrate,
            FrameDurationMs = 20,
            Application = OpusApplicationMode.GeneralAudio,
        };

        var factory = new OpusAudioCodecFactory();
        using var encoder = factory.CreateEncoder(settings);
        using var decoder = factory.CreateDecoder(stereo ? 2 : 1);

        encoder.SampleRate.Should().Be(48000);
        encoder.Channels.Should().Be(stereo ? 2 : 1);
        encoder.FrameSamples.Should().Be(960);

        const int frameSamples = 960;
        var channels = stereo ? 2 : 1;
        var pcm = new short[frameSamples * channels];

        // Encode 25 consecutive 20 ms frames (500 ms of audio) so the
        // codec has time to settle out of its warm-up state. Opus's first
        // couple of packets are quiet / partial by design.
        const int frameCount = 25;
        const double toneHz = 440.0;
        const double sampleRate = 48000.0;
        const double amplitude = 10_000.0;

        var encoded = new byte[frameCount][];
        var encodedTimestamps = new TimeSpan[frameCount];
        var ts = TimeSpan.Zero;
        var frameStep = TimeSpan.FromMilliseconds(settings.FrameDurationMs);

        for (var f = 0; f < frameCount; f++)
        {
            var baseSample = f * frameSamples;
            for (var i = 0; i < frameSamples; i++)
            {
                var phase = 2.0 * Math.PI * toneHz * (baseSample + i) / sampleRate;
                var s = (short)(Math.Sin(phase) * amplitude);
                for (var c = 0; c < channels; c++)
                {
                    pcm[i * channels + c] = s;
                }
            }

            var frame = encoder.EncodePcm(pcm, ts);
            frame.Should().NotBeNull($"frame {f} must encode");
            frame!.Value.Bytes.Length.Should().BeGreaterThan(0, $"frame {f} must have non-empty payload");
            frame.Value.Samples.Should().Be(frameSamples);
            frame.Value.Timestamp.Should().Be(ts, "encoder must propagate input timestamp verbatim");
            encoded[f] = frame.Value.Bytes;
            encodedTimestamps[f] = ts;
            ts += frameStep;
        }

        // Decode and concatenate all recovered PCM. Skip the first 2
        // frames (warmup) when measuring the dominant frequency — those
        // are typically silent or partial.
        var recovered = new System.Collections.Generic.List<short>(frameSamples * channels * frameCount);
        for (var f = 0; f < frameCount; f++)
        {
            var rtpTs = (uint)(f * frameSamples);
            var decoded = decoder.Decode(encoded[f], rtpTs);
            decoded.Should().NotBeNull($"frame {f} must decode");
            decoded!.Value.Samples.Should().Be(frameSamples);
            decoded.Value.Channels.Should().Be(channels);
            decoded.Value.SampleRate.Should().Be(48000);
            decoded.Value.RtpTimestamp.Should().Be(rtpTs);
            recovered.AddRange(decoded.Value.Pcm);
        }

        recovered.Count.Should().Be(frameSamples * channels * frameCount);

        // DFT-by-correlation on the first channel of the middle of the
        // stream (after warmup, well before the end). Correlate against
        // sin(2π·f·t) for candidate frequencies and find the peak.
        // Cheaper than a full FFT and we only care that 440 dominates.
        const int analysisStart = 3 * frameSamples;
        const int analysisLen = 16 * frameSamples; // ~320 ms
        var mono = new double[analysisLen];
        for (var i = 0; i < analysisLen; i++)
        {
            mono[i] = recovered[(analysisStart + i) * channels];
        }

        var best = DominantFrequency(mono, sampleRate, 200, 1000);
        best.Should().BeInRange(430, 450,
            $"440 Hz sine should survive roundtrip at {bitrate} bps / {(stereo ? "stereo" : "mono")}; got peak={best:F1} Hz");
    }

    [Fact]
    public void Encoder_rejects_wrong_sized_input()
    {
        var settings = new AudioSettings { Stereo = true };
        var factory = new OpusAudioCodecFactory();
        using var encoder = factory.CreateEncoder(settings);

        var tooSmall = new short[100];
        var act = () => encoder.EncodePcm(tooSmall, TimeSpan.Zero);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*expects exactly*");
    }

    [Fact]
    public void Invalid_frame_duration_throws_at_construction()
    {
        var settings = new AudioSettings { FrameDurationMs = 15 }; // not a valid Opus frame length
        var factory = new OpusAudioCodecFactory();
        var act = () => factory.CreateEncoder(settings);
        act.Should().Throw<ArgumentException>();
    }

    private static double DominantFrequency(double[] signal, double sampleRate, double minHz, double maxHz)
    {
        double bestFreq = 0;
        double bestEnergy = double.NegativeInfinity;
        // 1 Hz resolution is plenty — we're looking for a ±10 Hz bin.
        for (var f = minHz; f <= maxHz; f += 1.0)
        {
            double re = 0, im = 0;
            for (var i = 0; i < signal.Length; i++)
            {
                var phase = 2.0 * Math.PI * f * i / sampleRate;
                re += signal[i] * Math.Cos(phase);
                im += signal[i] * Math.Sin(phase);
            }
            var e = re * re + im * im;
            if (e > bestEnergy)
            {
                bestEnergy = e;
                bestFreq = f;
            }
        }
        return bestFreq;
    }
}
