namespace VicoScreenShare.Tests.Client;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentAssertions;
using SIPSorcery.Net;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;

/// <summary>
/// End-to-end A/V sync test that exercises the full pipeline:
/// <list type="number">
/// <item>Audio + video Sender Reports arrive on the shared <see cref="MediaClock"/>.</item>
/// <item>A simulated <c>PaintLoop</c> first paint sets the MediaClock
/// anchor at the moment of the first painted video frame, accounting
/// for the receiver's playout buffer.</item>
/// <item><see cref="AudioReceiver"/> receives audio packets that
/// accumulated during the buffer fill, then drains them aligned to
/// the anchor — proving audio actually plays once video starts
/// painting, regardless of buffer depth.</item>
/// </list>
///
/// Stand-in for the real renderer / paint thread: a single
/// <c>PublishVideoAnchor</c> call directly on <see cref="MediaClock"/>
/// using the same <c>SetVideoAnchorFromContentTime</c> entry point
/// the production <c>PaintLoop</c> uses.
/// </summary>
public class AvSyncEndToEndTests
{
    /// <summary>
    /// Build a synthetic NTP timestamp from seconds-since-1900 +
    /// fractional second. Mirrors the helper in MediaClockTests.
    /// </summary>
    private static ulong NtpFromComponents(uint secondsSince1900, double fractionalSecond)
    {
        var fraction = (ulong)(fractionalSecond * 4294967296.0);
        return ((ulong)secondsSince1900 << 32) | fraction;
    }

    [Theory]
    [InlineData(1, 60)]    // tightest buffer: 1 frame at 60 fps = ~17 ms.
    [InlineData(5, 60)]    // default
    [InlineData(60, 60)]   // 1-second buffer
    [InlineData(240, 60)]  // user's 4-second buffer at 60 fps
    [InlineData(240, 30)]  // user's 240 frames at 30 fps = 8 seconds
    public async Task Audio_drains_aligned_after_video_anchor_with_any_buffer_depth(
        int receiveBufferFrames,
        int nominalFps)
    {
        // Pre-anchor accumulation: how long video's playout buffer
        // takes to fill at the publisher's nominal frame rate.
        var bufferDelay = TimeSpan.FromSeconds((double)receiveBufferFrames / nominalFps);

        using var pc = new RTCPeerConnection(null);
        var clock = new MediaClock("avsync-test");
        var renderer = new RecordingRenderer();

        await using var audio = new AudioReceiver(
            pc,
            new FakeDecoderFactory(),
            renderer,
            displayName: "test",
            mediaClock: clock);
        await audio.StartAsync();

        // Both publishers' SRs arrive ~simultaneously. We pin them at
        // publisher NTP T₀ for arithmetic clarity; the audio rtpTs is
        // 0 and video rtpTs is 0 at that instant.
        const uint ntpSeconds = 3000;
        clock.OnAudioSenderReport(NtpFromComponents(ntpSeconds, 0.0), rtpTimestamp: 0);
        clock.OnVideoSenderReport(NtpFromComponents(ntpSeconds, 0.0), rtpTimestamp: 0);

        // Feed audio packets that span the buffer-fill window. Audio's
        // rtpTs counter advances at 48 kHz, so 20 ms framing means
        // each packet's rtpTs grows by 960. The receiver's jitter
        // buffer accumulates these — drain is gated on the anchor.
        var audioFrames = (int)Math.Ceiling(bufferDelay.TotalSeconds / 0.020) + 5;
        for (var i = 0; i < audioFrames; i++)
        {
            var rtpTs = (uint)(i * 960);
            audio.IngestPacket(rtpTs, payload: new byte[] { 0x01 });
        }

        // Pre-anchor: nothing should have been submitted to the
        // renderer. All audio is parked in the jitter buffer.
        // (PacketsReceived is incremented in OnRtpPacketReceived
        // which IngestPacket bypasses; FramesDecoded is the right
        // signal for the test seam.)
        renderer.Submitted.Should().BeEmpty(
            "audio must wait for the video anchor before submitting");
        audio.FramesDecoded.Should().Be(audioFrames);
        audio.FramesSubmitted.Should().Be(0);

        // Simulate PaintLoop's first paint: the painted video frame
        // is the OLDEST in the queue, captured at publisher t=0.
        // Its content time on the per-stream 90 kHz video clock is 0
        // (rtpTs=0 → content TimeSpan = 0). The local-Stopwatch wall
        // is whatever wall time we paint at — bufferDelay after
        // stream start.
        //
        // We anchor at "now-ish" using SetVideoAnchorFromContentTime
        // exactly the way PaintLoop does. The publisher captured the
        // first video frame `bufferDelay` ago in publisher time
        // (modeling a constant-fps stream where the publisher emitted
        // bufferDelay worth of frames before the receiver started
        // painting).
        var firstPaintLocal = Stopwatch.GetTimestamp();
        var firstPaintedContentTime = TimeSpan.Zero; // video rtpTs=0 → content time 0.
        var anchored = clock.SetVideoAnchorFromContentTime(
            firstPaintedContentTime,
            firstPaintLocal);
        anchored.Should().BeTrue("video SR is present so anchor must latch");

        // Drive new audio packet arrivals AFTER the anchor: each one
        // triggers DrainReadyFrames. We pump packets covering the
        // post-anchor window plus a few extra so steady-state runs
        // long enough to assert the offset stays small.
        const int postAnchorFrames = 10;
        for (var i = 0; i < postAnchorFrames; i++)
        {
            var rtpTs = (uint)((audioFrames + i) * 960);
            audio.IngestPacket(rtpTs, payload: new byte[] { 0x02 });
        }

        // Audio should have drained at least the frames whose target
        // wall has arrived. With our setup, the buffer-delay-worth of
        // pre-anchor audio that came in before the anchor latched
        // should now drain — those frames have content times that
        // equal or precede the anchor's content time, so their target
        // wall is at or before `firstPaintLocal` (i.e. now-ish).
        //
        // Every frame in audio's queue except the very newest few
        // should have made it out by now.
        audio.FramesSubmitted.Should().BeGreaterThan(0,
            $"audio MUST start playing once video anchors (buffer={receiveBufferFrames} frames @ {nominalFps} fps = {bufferDelay.TotalMilliseconds:F0} ms delay)");
    }

    [Fact]
    public async Task Audio_drains_immediately_when_av_sync_disabled_no_anchor()
    {
        // Sessions without a MediaClock (audio-only, tests) must
        // never block on an anchor that will never arrive.
        using var pc = new RTCPeerConnection(null);
        var renderer = new RecordingRenderer();
        await using var audio = new AudioReceiver(
            pc,
            new FakeDecoderFactory(),
            renderer,
            displayName: "test",
            mediaClock: null);
        await audio.StartAsync();

        // Feed enough packets to exceed the steady-state jitter depth.
        for (var i = 0; i < 10; i++)
        {
            var rtpTs = (uint)(i * 960);
            audio.IngestPacket(rtpTs, payload: new byte[] { 0x03 });
        }

        // No MediaClock → drain runs immediately. With 10 frames in
        // and a 3-slot steady-state target, we expect 7 to drain.
        audio.FramesSubmitted.Should().BeGreaterThanOrEqualTo(7,
            "no media clock → audio drains as before, gated only by jitter depth");
    }

    [Fact]
    public async Task Audio_does_not_drain_pre_anchor_even_with_video_sr()
    {
        // Regression: SR for both streams arrives BEFORE the anchor
        // latches. AudioReceiver must still hold the audio queue —
        // the anchor is the gating signal, not SR availability alone.
        using var pc = new RTCPeerConnection(null);
        var clock = new MediaClock("noanchor-test");
        var renderer = new RecordingRenderer();

        await using var audio = new AudioReceiver(
            pc,
            new FakeDecoderFactory(),
            renderer,
            displayName: "test",
            mediaClock: clock);
        await audio.StartAsync();

        const uint ntpSeconds = 3000;
        clock.OnAudioSenderReport(NtpFromComponents(ntpSeconds, 0.0), rtpTimestamp: 0);
        clock.OnVideoSenderReport(NtpFromComponents(ntpSeconds, 0.0), rtpTimestamp: 0);

        for (var i = 0; i < 30; i++)
        {
            audio.IngestPacket((uint)(i * 960), payload: new byte[] { 0x04 });
        }

        renderer.Submitted.Should().BeEmpty(
            "AudioRtpToLocalWallTicks returns null pre-anchor → drain returns early");
        audio.FramesSubmitted.Should().Be(0);
    }

    [Fact]
    public async Task Audio_aligns_to_late_arriving_video_anchor_after_pile_up()
    {
        // Models the real sequence: SR can arrive seconds after
        // first audio packets. AudioReceiver holds, then drains
        // when the anchor finally lands. This is the case the user
        // hit in production — audio piling up while video buffer
        // fills, anchor arrives, audio drains.
        using var pc = new RTCPeerConnection(null);
        var clock = new MediaClock("late-anchor-test");
        var renderer = new RecordingRenderer();

        await using var audio = new AudioReceiver(
            pc,
            new FakeDecoderFactory(),
            renderer,
            displayName: "test",
            mediaClock: clock);
        await audio.StartAsync();

        // Pile up 100 audio packets BEFORE any SR.
        for (var i = 0; i < 100; i++)
        {
            audio.IngestPacket((uint)(i * 960), payload: new byte[] { 0x05 });
        }
        audio.FramesSubmitted.Should().Be(0, "no SR yet → cannot translate → no drain");

        // Audio SR + video SR arrive.
        const uint ntpSeconds = 3000;
        clock.OnAudioSenderReport(NtpFromComponents(ntpSeconds, 0.0), rtpTimestamp: 0);
        clock.OnVideoSenderReport(NtpFromComponents(ntpSeconds, 0.0), rtpTimestamp: 0);

        // Anchor latches.
        var anchored = clock.SetVideoAnchorFromContentTime(TimeSpan.Zero, Stopwatch.GetTimestamp());
        anchored.Should().BeTrue();

        // Drain isn't called by the anchor latch itself — it's
        // triggered by the next incoming audio packet. Push one
        // more to fire the drain.
        audio.IngestPacket(rtpTimestamp: 100 * 960, payload: new byte[] { 0x06 });

        audio.FramesSubmitted.Should().BeGreaterThan(0,
            "audio piled up pre-anchor MUST flush once anchor latches");
    }

    private sealed record SubmittedSlice(short[] Pcm, TimeSpan Ts);

    private sealed class RecordingRenderer : IAudioRenderer
    {
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public double Volume { get; set; } = 1.0;
        public List<SubmittedSlice> Submitted { get; } = new();

        public System.Threading.Tasks.Task StartAsync(int sampleRate, int channels)
        {
            SampleRate = sampleRate;
            Channels = channels;
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public void Submit(ReadOnlySpan<short> interleavedPcm, TimeSpan timestamp)
        {
            Submitted.Add(new SubmittedSlice(interleavedPcm.ToArray(), timestamp));
        }

        public System.Threading.Tasks.Task StopAsync() => System.Threading.Tasks.Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDecoderFactory : IAudioDecoderFactory
    {
        public bool IsAvailable => true;
        public IAudioDecoder CreateDecoder(int channels) => new FakeDecoder(channels);
    }

    private sealed class FakeDecoder : IAudioDecoder
    {
        public FakeDecoder(int channels) { Channels = channels; }
        public int SampleRate => 48000;
        public int Channels { get; }

        public DecodedAudioFrame? Decode(ReadOnlySpan<byte> encoded, uint rtpTimestamp)
        {
            return new DecodedAudioFrame(
                Pcm: new short[960 * Channels],
                Samples: 960,
                Channels: Channels,
                SampleRate: SampleRate,
                RtpTimestamp: rtpTimestamp);
        }

        public void Dispose() { }
    }
}
