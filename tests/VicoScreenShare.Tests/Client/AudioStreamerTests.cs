namespace VicoScreenShare.Tests.Client;

using System;
using System.Collections.Generic;
using FluentAssertions;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;

/// <summary>
/// Proves <see cref="AudioStreamer"/> slices arbitrary-size WASAPI
/// buffers into exact-size (960-sample stereo) Opus frames, preserves
/// the capture timestamp on every encoded packet, and does not leak
/// samples across Start / Stop cycles.
/// </summary>
public class AudioStreamerTests
{
    [Fact]
    public void Variable_sized_buffers_produce_exact_960_sample_frames()
    {
        var source = new FakeAudioCaptureSource();
        var resampler = new PassThroughResampler();
        var encoder = new CountingEncoder(frameSamples: 960, channels: 2);
        var factory = new FixedEncoderFactory(encoder);

        var emitted = new List<EmittedFrame>();
        void OnEncoded(uint duration, byte[] bytes, TimeSpan ts)
        {
            emitted.Add(new EmittedFrame(duration, bytes.Length, ts));
        }

        using var streamer = new AudioStreamer(
            source, resampler, OnEncoded,
            new AudioSettings { Stereo = true },
            factory);
        streamer.Start();

        // Feed buffers with odd sample counts: 500, 1200, 1000, 2000,
        // 300 samples per channel × 2 channels. Total = 5000 × 2 = 10000
        // short samples. 10000 / 1920 = 5 complete stereo frames, with
        // 400 shorts (200 samples/ch) left in the accumulator.
        var timestamps = new[]
        {
            TimeSpan.FromMilliseconds(0),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(35),
            TimeSpan.FromMilliseconds(55),
            TimeSpan.FromMilliseconds(97),
        };
        var sizes = new[] { 500, 1200, 1000, 2000, 300 };
        for (var i = 0; i < sizes.Length; i++)
        {
            source.EmitSilence(sizes[i], channels: 2, sampleRate: 48000, timestamp: timestamps[i]);
        }

        emitted.Should().HaveCount(5);
        emitted.Should().AllSatisfy(f =>
        {
            f.DurationRtp.Should().Be(960);
        });
        encoder.ObservedFrameLengths.Should().AllSatisfy(len =>
        {
            len.Should().Be(1920, "stereo × 960 samples");
        });
    }

    [Fact]
    public void Stop_stops_encoding_new_buffers()
    {
        var source = new FakeAudioCaptureSource();
        var resampler = new PassThroughResampler();
        var encoder = new CountingEncoder(frameSamples: 960, channels: 2);
        var factory = new FixedEncoderFactory(encoder);
        var emitted = 0;

        using var streamer = new AudioStreamer(
            source, resampler, (_, _, _) => emitted++,
            new AudioSettings { Stereo = true },
            factory);
        streamer.Start();

        // Enough for exactly 2 frames.
        source.EmitSilence(samplesPerChannel: 960 * 2, channels: 2, sampleRate: 48000, timestamp: TimeSpan.Zero);
        emitted.Should().Be(2);

        streamer.Stop();
        // New buffers after Stop should be ignored.
        source.EmitSilence(samplesPerChannel: 960, channels: 2, sampleRate: 48000, timestamp: TimeSpan.FromMilliseconds(50));
        emitted.Should().Be(2, "Stop must detach the FrameArrived handler");
    }

    [Fact]
    public void Timestamp_is_propagated_verbatim_from_capture()
    {
        var source = new FakeAudioCaptureSource();
        var resampler = new PassThroughResampler();
        var encoder = new CountingEncoder(frameSamples: 960, channels: 2);
        var factory = new FixedEncoderFactory(encoder);
        var emittedTimestamps = new List<TimeSpan>();

        using var streamer = new AudioStreamer(
            source, resampler, (_, _, ts) => emittedTimestamps.Add(ts),
            new AudioSettings { Stereo = true },
            factory);
        streamer.Start();

        var ts = TimeSpan.FromMilliseconds(123);
        source.EmitSilence(samplesPerChannel: 960, channels: 2, sampleRate: 48000, timestamp: ts);

        emittedTimestamps.Should().ContainSingle().Which.Should().Be(ts);
    }

    private readonly record struct EmittedFrame(uint DurationRtp, int ByteLength, TimeSpan Timestamp);

    /// <summary>Minimal <see cref="IAudioCaptureSource"/> that fires
    /// <see cref="FrameArrived"/> on demand with a silent PCM buffer.
    /// </summary>
    private sealed class FakeAudioCaptureSource : IAudioCaptureSource
    {
        public string DisplayName => "fake";
        public int SourceSampleRate => 48000;
        public int SourceChannels => 2;
        public AudioSampleFormat SourceFormat => AudioSampleFormat.PcmS16Interleaved;
        public event AudioFrameArrivedHandler? FrameArrived;
        public event Action? Closed;

        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitSilence(int samplesPerChannel, int channels, int sampleRate, TimeSpan timestamp)
        {
            // S16 interleaved zeros.
            var bytes = new byte[samplesPerChannel * channels * sizeof(short)];
            var frame = new AudioFrameData(
                bytes,
                sampleRate,
                channels,
                AudioSampleFormat.PcmS16Interleaved,
                timestamp);
            FrameArrived?.Invoke(in frame);
            _ = Closed; // suppress unused warning
        }
    }

    /// <summary>Resampler that copies S16 input to the destination
    /// without rate conversion. Good enough for tests that already
    /// supply 48 kHz S16 input; shape of the conversion is exercised
    /// by the real NAudioResampler harness tests.</summary>
    private sealed class PassThroughResampler : IAudioResampler
    {
        public int Resample(ReadOnlySpan<byte> inputPcm, int inputSampleRate, int inputChannels, AudioSampleFormat inputFormat, Span<short> destination)
        {
            if (inputFormat != AudioSampleFormat.PcmS16Interleaved)
            {
                throw new NotSupportedException("pass-through only for S16 in tests");
            }
            var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(inputPcm);
            src.CopyTo(destination);
            return src.Length;
        }

        public void Dispose() { }
    }

    private sealed class CountingEncoder : IAudioEncoder
    {
        public CountingEncoder(int frameSamples, int channels)
        {
            FrameSamples = frameSamples;
            Channels = channels;
        }

        public int SampleRate => 48000;
        public int Channels { get; }
        public int FrameSamples { get; }
        public List<int> ObservedFrameLengths { get; } = new();

        public EncodedAudioFrame? EncodePcm(ReadOnlySpan<short> pcm, TimeSpan inputTimestamp)
        {
            ObservedFrameLengths.Add(pcm.Length);
            // Non-empty payload so the streamer emits it.
            return new EncodedAudioFrame(new byte[] { 0xFC }, inputTimestamp, FrameSamples);
        }

        public void Dispose() { }
    }

    private sealed class FixedEncoderFactory : IAudioEncoderFactory
    {
        private readonly IAudioEncoder _enc;
        public FixedEncoderFactory(IAudioEncoder enc) { _enc = enc; }
        public bool IsAvailable => true;
        public IAudioEncoder CreateEncoder(AudioSettings settings) => _enc;
    }
}
