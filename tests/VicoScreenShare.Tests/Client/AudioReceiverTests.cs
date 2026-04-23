namespace VicoScreenShare.Tests.Client;

using System;
using System.Collections.Generic;
using FluentAssertions;
using SIPSorcery.Net;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;

/// <summary>
/// Proves <see cref="AudioReceiver"/> reorders out-of-order packets by
/// RTP timestamp, drops duplicates, and pushes drained PCM into the
/// renderer in playout order.
/// </summary>
public class AudioReceiverTests
{
    [Fact]
    public async Task Out_of_order_packets_are_drained_in_rtp_timestamp_order()
    {
        using var pc = new RTCPeerConnection(null);
        var decoderFactory = new FakeDecoderFactory();
        var renderer = new RecordingRenderer();

        await using var receiver = new AudioReceiver(pc, decoderFactory, renderer, "test");
        await receiver.StartAsync();

        // Feed packets in shuffled order. The receiver holds 3 slots
        // before it starts draining, so only the 4th+ packets produce
        // submits.
        uint[] arrivalOrder = { 960, 2880, 1920, 3840, 4800, 5760 };
        foreach (var ts in arrivalOrder)
        {
            receiver.IngestPacket(ts, TaggedPayload(ts));
        }

        // With 6 packets and a 3-slot buffer, exactly 3 get drained.
        renderer.Submitted.Should().HaveCount(3);
        var drainedRtpOrder = ExtractRtpTimestamps(renderer.Submitted);
        drainedRtpOrder.Should().BeInAscendingOrder("jitter buffer must drain in RTP order regardless of arrival order");
    }

    [Fact]
    public async Task Duplicate_rtp_timestamps_are_dropped()
    {
        using var pc = new RTCPeerConnection(null);
        var decoderFactory = new FakeDecoderFactory();
        var renderer = new RecordingRenderer();

        await using var receiver = new AudioReceiver(pc, decoderFactory, renderer, "test");
        await receiver.StartAsync();

        // Same timestamp three times plus filler to force drain.
        receiver.IngestPacket(960, TaggedPayload(960));
        receiver.IngestPacket(960, TaggedPayload(960));
        receiver.IngestPacket(960, TaggedPayload(960));
        receiver.IngestPacket(1920, TaggedPayload(1920));
        receiver.IngestPacket(2880, TaggedPayload(2880));
        receiver.IngestPacket(3840, TaggedPayload(3840));

        receiver.FramesDropped.Should().BeGreaterThanOrEqualTo(2, "two duplicates should have been dropped");
        // Only one instance of RTP ts=960 survives to be submitted.
        var drained = ExtractRtpTimestamps(renderer.Submitted);
        drained.Should().NotContain(x => drained.IndexOf(x) != drained.LastIndexOf(x),
            "no timestamp should appear twice in submitted output");
    }

    [Fact]
    public async Task Late_reorder_past_drain_cursor_is_dropped()
    {
        using var pc = new RTCPeerConnection(null);
        var decoderFactory = new FakeDecoderFactory();
        var renderer = new RecordingRenderer();

        await using var receiver = new AudioReceiver(pc, decoderFactory, renderer, "test");
        await receiver.StartAsync();

        // Feed enough packets to force the first drain at ts=960.
        receiver.IngestPacket(960, TaggedPayload(960));
        receiver.IngestPacket(1920, TaggedPayload(1920));
        receiver.IngestPacket(2880, TaggedPayload(2880));
        receiver.IngestPacket(3840, TaggedPayload(3840));
        renderer.Submitted.Should().NotBeEmpty();

        // Now feed a late packet that arrives after the drain cursor.
        // It should NOT be submitted — that would play audio out of order.
        var preDropCount = receiver.FramesDropped;
        receiver.IngestPacket(960, TaggedPayload(960));
        receiver.FramesDropped.Should().BeGreaterThan(preDropCount);
    }

    private static byte[] TaggedPayload(uint tag)
    {
        // Arbitrary non-empty payload; the fake decoder echoes it with
        // an embedded tag so we can assert drain order by matching the
        // tag in the submitted PCM.
        var bytes = new byte[4];
        BitConverter.GetBytes(tag).CopyTo(bytes, 0);
        return bytes;
    }

    private static List<uint> ExtractRtpTimestamps(IReadOnlyList<SubmittedSlice> submitted)
    {
        var result = new List<uint>(submitted.Count);
        foreach (var s in submitted)
        {
            // Every PCM sample in the slice carries the tag in its first
            // short (see FakeDecoder).
            result.Add(unchecked((uint)(ushort)s.Pcm[0] | ((uint)(ushort)s.Pcm[1] << 16)));
        }
        return result;
    }

    private sealed record SubmittedSlice(short[] Pcm, TimeSpan Ts);

    private sealed class RecordingRenderer : IAudioRenderer
    {
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public List<SubmittedSlice> Submitted { get; } = new();

        public Task StartAsync(int sampleRate, int channels)
        {
            SampleRate = sampleRate;
            Channels = channels;
            return Task.CompletedTask;
        }

        public void Submit(ReadOnlySpan<short> interleavedPcm, TimeSpan timestamp)
        {
            Submitted.Add(new SubmittedSlice(interleavedPcm.ToArray(), timestamp));
        }

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDecoderFactory : IAudioDecoderFactory
    {
        public bool IsAvailable => true;
        public IAudioDecoder CreateDecoder(int channels) => new FakeDecoder(channels);
    }

    /// <summary>Decoder that embeds the RTP timestamp into the first
    /// 2 shorts of the PCM frame so tests can correlate submitted
    /// output back to input order.</summary>
    private sealed class FakeDecoder : IAudioDecoder
    {
        public FakeDecoder(int channels) { Channels = channels; }
        public int SampleRate => 48000;
        public int Channels { get; }

        public DecodedAudioFrame? Decode(ReadOnlySpan<byte> encoded, uint rtpTimestamp)
        {
            var pcm = new short[960 * Channels];
            // Encode RTP timestamp into the first two shorts so the
            // tests can assert arrival order from submitted output.
            pcm[0] = unchecked((short)(rtpTimestamp & 0xFFFF));
            pcm[1] = unchecked((short)((rtpTimestamp >> 16) & 0xFFFF));
            return new DecodedAudioFrame(pcm, 960, Channels, SampleRate, rtpTimestamp);
        }

        public void Dispose() { }
    }
}
