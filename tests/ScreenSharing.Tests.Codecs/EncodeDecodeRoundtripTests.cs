using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Windows.Media.Codecs;

namespace ScreenSharing.Tests.Codecs;

/// <summary>
/// End-to-end correctness tests for the MF H.264 encoder + decoder
/// roundtrip. The capture-test diagnostic showed that the production
/// receive path (queue + PTS pacer + render) is smooth when fed raw
/// captured frames, but jittery when the same content goes through
/// encode → decode first. These tests pin down exactly what the codec
/// pair preserves and what it doesn't:
///
///   * timestamp propagation through encoder (input → output SampleTime)
///   * timestamp propagation through decoder (input → output SampleTime)
///   * end-to-end timestamp roundtrip (input → encode → decode → output)
///   * frame count integrity (no silent drops)
///   * monotonic ordering (no reordering / out-of-order bursts)
///   * inter-frame delta preservation (uniform AND non-uniform patterns)
///   * BGRA pixel content roundtrip with a lossy-codec tolerance
///   * behavior under the alternating 14/21 ms picker pattern that the
///     production sender produces at 60 fps from a 144 Hz source
///
/// The encoder is async (NVENC pump or software MFT pump), so tests
/// account for ~1 frame of pipeline delay and flush remaining outputs
/// after the input phase. The decoder is mostly sync but can buffer 1
/// frame for SPS/PPS at startup.
/// </summary>
public class EncodeDecodeRoundtripTests : IDisposable
{
    private const int Width = 320;
    private const int Height = 240;
    private const int BitrateBps = 5_000_000;

    private readonly IVideoEncoder _encoder;
    private readonly IVideoDecoder _decoder;

    public EncodeDecodeRoundtripTests()
    {
        MediaFoundationRuntime.EnsureInitialized();
        if (!MediaFoundationRuntime.IsAvailable)
        {
            throw new InvalidOperationException(
                $"Media Foundation not available — codec tests cannot run. Reason: {MediaFoundationRuntime.LastError}");
        }
        _encoder = new MediaFoundationH264Encoder(Width, Height, fps: 60, bitrate: BitrateBps);
        _decoder = new MediaFoundationH264DecoderFactory().CreateDecoder();
    }

    public void Dispose()
    {
        try { _encoder.Dispose(); } catch { }
        try { _decoder.Dispose(); } catch { }
    }

    // -------------------------------------------------------------------
    // Encoder-only tests
    // -------------------------------------------------------------------

    [Fact]
    public void Encoder_propagates_input_timestamp_to_output()
    {
        // Submit 60 frames with a uniform 16.67 ms cadence, drain the
        // encoder's async pump, verify every output's Timestamp matches
        // an input timestamp exactly. The encoder must not synthesize
        // its own SampleTime values from a frame counter.
        var inputs = MakeUniformTimestamps(60, 16.67);
        var outputs = EncodeAll(inputs);

        outputs.Should().NotBeEmpty(
            "encoder should produce at least some output for 60 input frames");
        foreach (var ef in outputs)
        {
            inputs.Should().ContainEquivalentOf(ef.Timestamp,
                because: "every encoder output's timestamp must come from one of the inputs");
        }
    }

    [Fact]
    public void Encoder_output_timestamps_are_monotonically_increasing()
    {
        var inputs = MakeUniformTimestamps(60, 16.67);
        var outputs = EncodeAll(inputs);

        outputs.Should().HaveCountGreaterThan(1);
        for (var i = 1; i < outputs.Count; i++)
        {
            outputs[i].Timestamp.Should().BeGreaterThan(outputs[i - 1].Timestamp,
                $"output #{i} must come strictly after output #{i - 1}");
        }
    }

    [Fact]
    public void Encoder_preserves_alternating_14_21_ms_pattern_from_picker()
    {
        // The production source picker produces alternating 14 / 21 ms
        // gaps when reducing 144 fps to 60 fps. The encoder must
        // propagate those exact deltas — if it slaps a uniform synthetic
        // SampleTime on the outputs the receiver pacer will play motion
        // at the wrong rate.
        var inputs = MakeAlternatingTimestamps(60, 13.89, 20.83);
        var outputs = EncodeAll(inputs);

        var inputDeltas = ComputeDeltasMs(inputs).ToList();
        var outputDeltas = ComputeDeltasMs(outputs.Select(o => o.Timestamp).ToList()).ToList();

        outputDeltas.Should().NotBeEmpty();
        // Output deltas should be a contiguous subsequence of the input
        // deltas (the encoder may swallow the first frame for warmup,
        // but everything after that must match).
        for (var i = 0; i < outputDeltas.Count; i++)
        {
            outputDeltas[i].Should().BeApproximately(inputDeltas[i], precision: 0.01,
                because: $"output delta #{i} must equal input delta #{i} exactly (no requantization)");
        }
    }

    [Fact]
    public void Encoder_produces_close_to_one_output_per_input()
    {
        // After encoder warmup, the steady-state output count should
        // closely match the input count. Drops here mean rate-control
        // dropped frames or the async pump's queue lost something.
        const int n = 60;
        var inputs = MakeUniformTimestamps(n, 16.67);
        var outputs = EncodeAll(inputs);

        // Allow up to 5 frames of warmup loss — NVENC and the software
        // MFT both need a few frames before producing the first IDR.
        outputs.Count.Should().BeGreaterOrEqualTo(n - 5,
            $"encoder should produce close to {n} outputs for {n} inputs");
        outputs.Count.Should().BeLessOrEqualTo(n,
            "encoder should not produce more outputs than it received inputs");
    }

    // -------------------------------------------------------------------
    // Decoder-only tests
    // -------------------------------------------------------------------

    [Fact]
    public void Decoder_propagates_input_timestamp_to_output()
    {
        // Encode a known sequence first, then feed the encoded bytes to
        // the decoder one at a time and verify the decoder propagates
        // SampleTime through. Every DecodedVideoFrame must carry a
        // timestamp that came from one of the encoded inputs.
        var inputs = MakeUniformTimestamps(60, 16.67);
        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        decoded.Should().NotBeEmpty();
        foreach (var df in decoded)
        {
            encoded.Select(e => e.Timestamp).Should().Contain(df.Timestamp,
                because: "every decoded timestamp must come from an encoded input");
        }
    }

    [Fact]
    public void Decoder_output_timestamps_are_monotonically_increasing()
    {
        var inputs = MakeUniformTimestamps(60, 16.67);
        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        decoded.Should().HaveCountGreaterThan(1);
        for (var i = 1; i < decoded.Count; i++)
        {
            decoded[i].Timestamp.Should().BeGreaterThan(decoded[i - 1].Timestamp,
                $"decoded frame #{i} must come strictly after #{i - 1}");
        }
    }

    [Fact]
    public void Decoder_does_not_lose_frames_under_steady_state()
    {
        var inputs = MakeUniformTimestamps(60, 16.67);
        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        // Decoder warmup is typically 1 frame for SPS/PPS parsing.
        decoded.Count.Should().BeGreaterOrEqualTo(encoded.Count - 3,
            $"decoder should produce close to {encoded.Count} outputs for {encoded.Count} inputs");
    }

    // -------------------------------------------------------------------
    // End-to-end roundtrip tests (THIS IS THE KEY ONE)
    // -------------------------------------------------------------------

    [Fact]
    public void Roundtrip_preserves_every_input_timestamp()
    {
        // The most important test for the smoothness bug: after a full
        // encode → decode roundtrip, do the decoded frames carry the
        // exact same timestamps the inputs had? If timestamps are lost
        // or remapped here, the receiver-side PTS pacer cannot align
        // motion correctly and the output looks jittery even though
        // counts and ordering are fine.
        var inputs = MakeUniformTimestamps(60, 16.67);
        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        var decodedTs = decoded.Select(d => d.Timestamp).ToHashSet();
        // Allow up to 5 frames of warmup loss but every surviving
        // output's timestamp must match an input.
        decoded.Should().HaveCountGreaterOrEqualTo(inputs.Count - 5);
        foreach (var df in decoded)
        {
            inputs.Should().Contain(df.Timestamp,
                $"decoded timestamp {df.Timestamp.TotalMilliseconds:F2} ms must come from an input");
        }
    }

    [Fact]
    public void Roundtrip_preserves_alternating_14_21_ms_pattern()
    {
        // The actual scenario from production: input frames at
        // alternating 13.89 / 20.83 ms gaps (the source picker output
        // for 60 fps target on 144 Hz monitor). After encode + decode
        // the deltas must be preserved exactly — any requantization or
        // rounding here would shift content moments away from where the
        // pacer expects to paint them.
        var inputs = MakeAlternatingTimestamps(120, 13.89, 20.83);
        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        var inputDeltas = ComputeDeltasMs(inputs).ToList();
        var decodedDeltas = ComputeDeltasMs(decoded.Select(d => d.Timestamp).ToList()).ToList();

        decodedDeltas.Should().NotBeEmpty();
        decodedDeltas.Count.Should().BeGreaterOrEqualTo(inputDeltas.Count - 5);
        for (var i = 0; i < decodedDeltas.Count; i++)
        {
            decodedDeltas[i].Should().BeApproximately(inputDeltas[i], precision: 0.01,
                $"roundtrip delta #{i} must equal input delta #{i}");
        }
    }

    [Fact]
    public void Roundtrip_preserves_monotonic_order()
    {
        var inputs = MakeUniformTimestamps(60, 16.67);
        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        var decodedTs = decoded.Select(d => d.Timestamp).ToList();
        decodedTs.Should().BeInAscendingOrder("decoder must not reorder frames");
    }

    [Fact]
    public void Roundtrip_does_not_duplicate_timestamps()
    {
        // If the encoder or decoder emits two outputs with the same
        // timestamp, the pacer would try to paint two frames at the
        // same wall time, which is a likely source of visible bursts.
        var inputs = MakeUniformTimestamps(60, 16.67);
        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        var distinct = decoded.Select(d => d.Timestamp).Distinct().Count();
        distinct.Should().Be(decoded.Count,
            "decoded timestamps must all be unique — duplicates indicate the codec is " +
            "emitting two frames for one source moment, which would burst-paint at the receiver");
    }

    [Fact]
    public void Roundtrip_count_matches_input_count_within_warmup_tolerance()
    {
        const int n = 60;
        var inputs = MakeUniformTimestamps(n, 16.67);
        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        // ~5 frames warmup at the encoder + ~1 at the decoder = ~6 max loss.
        decoded.Count.Should().BeGreaterOrEqualTo(n - 8,
            $"roundtrip should produce close to {n} frames for {n} inputs");
        decoded.Count.Should().BeLessOrEqualTo(n,
            "roundtrip should not invent frames that weren't submitted");
    }

    // -------------------------------------------------------------------
    // Visual content tests
    // -------------------------------------------------------------------

    // -------------------------------------------------------------------
    // Release-pattern tests (the real jitter suspect)
    // -------------------------------------------------------------------

    [Fact]
    public void Decoder_releases_frames_one_at_a_time_in_steady_state()
    {
        // The smoothness suspect: if the decoder sits on N inputs and
        // then releases them in a burst (e.g. 0,0,0,3,0,0,0,3,...), the
        // receiver's PTS pacer will see burst arrivals at the input queue
        // instead of a steady 1-per-Decode cadence. Even if every frame's
        // SampleTime is correct, burst release would starve the pacer
        // between bursts and flood it during them.
        //
        // Production baseline we want to see: one decoded frame per
        // Decode() call in steady state, with at most 1-2 calls at the
        // very start returning 0 while the decoder waits for SPS/PPS.
        var inputs = MakeUniformTimestamps(120, 16.67);
        var encoded = EncodeAll(inputs);

        var perCallCounts = new List<int>(encoded.Count);
        foreach (var ef in encoded)
        {
            var frames = _decoder.Decode(ef.Bytes, ef.Timestamp);
            perCallCounts.Add(frames.Count);
        }
        // Don't count drain-phase calls — we only care about steady-state
        // behavior during normal streaming.

        // Skip the first 5 calls (warmup — decoder waits for SPS/PPS
        // and may return 0 frames until the first keyframe is parsed).
        var steady = perCallCounts.Skip(5).ToList();

        var zeros = steady.Count(c => c == 0);
        var ones = steady.Count(c => c == 1);
        var bursts = steady.Count(c => c >= 2);
        var maxBurst = steady.Count > 0 ? steady.Max() : 0;

        // Diagnostic values so the test output shows the release shape.
        var diagnostic = $"zeros={zeros} ones={ones} bursts>=2={bursts} maxBurst={maxBurst} of {steady.Count}";

        // The healthy shape is "almost every call returns 1". If the MF
        // decoder is sitting on a reorder buffer and releasing in bursts,
        // zeros and bursts dominate instead.
        ones.Should().BeGreaterThan(steady.Count / 2,
            $"most Decode() calls in steady state should yield exactly one frame — {diagnostic}");

        maxBurst.Should().BeLessOrEqualTo(2,
            $"a single Decode() call should never yield a large burst — {diagnostic}");
    }

    [Fact]
    public void Decoder_latency_is_low_enough_for_low_latency_streaming()
    {
        // Measure pipeline latency: how many Decode() calls does the
        // decoder need to see before it produces its FIRST output? Low
        // latency H.264 should produce the first frame within 1-2 calls
        // (just the SPS/PPS parse). Larger delays indicate the decoder
        // is operating in a non-low-latency mode where it waits to
        // gather a reorder group before producing anything.
        var inputs = MakeUniformTimestamps(60, 16.67);
        var encoded = EncodeAll(inputs);

        var firstOutputCallIndex = -1;
        for (var i = 0; i < encoded.Count; i++)
        {
            var frames = _decoder.Decode(encoded[i].Bytes, encoded[i].Timestamp);
            if (frames.Count > 0 && firstOutputCallIndex < 0)
            {
                firstOutputCallIndex = i;
                break;
            }
        }

        firstOutputCallIndex.Should().BeGreaterOrEqualTo(0,
            "decoder should eventually produce at least one output");
        firstOutputCallIndex.Should().BeLessOrEqualTo(3,
            "low-latency H.264 decoder should yield its first frame within ~2 inputs — any larger initial lag means the decoder is buffering a reorder group");
    }

    // -------------------------------------------------------------------
    // Per-call wall-clock latency tests.
    //
    // The codec pair passes every correctness test — timestamps, content,
    // ordering, counts — and the production debug log shows every decoder
    // Decode() call returning count=1, i.e. a clean 1-in/1-out stream.
    // Yet the encode/decode capture-test mode visibly jitters while the
    // texture-direct mode through the same renderer/pacer is smooth.
    //
    // That leaves *timing variance* — not correctness — as the suspect.
    // If decode of frame N takes 5 ms and decode of frame N+1 takes 30 ms,
    // both decoded frames land in the renderer queue with correct PTS,
    // but their wall-clock inter-arrival gap is 16.67 + (30 - 5) = 41 ms
    // for one pair and 16.67 + (5 - 30) = -8 ms for the next. The pacer
    // anchors paints on PTS but falls back to "paint immediately when
    // already late" when the queued frame's dueWall is in the past — a
    // latency spike bigger than the 30 ms prebuffer kicks paints into the
    // late-arrival branch, producing the bimodal paint cadence that the
    // eye reads as jitter.
    //
    // These tests measure the per-call time of ProcessInput→DrainOutput
    // for both encoder and decoder, and flag any call whose latency is
    // large enough to push the pacer into the late branch.
    // -------------------------------------------------------------------

    [Fact]
    public void Decoder_per_call_latency_does_not_spike_above_pacer_prebuffer()
    {
        // Decoder_per_call latency must stay small enough that no single
        // Decode() call takes longer than the receiver pacer's prebuffer
        // margin (30 ms). If it does, that frame's paint wall time will
        // be past its scheduled dueWall and the pacer will paint it
        // immediately instead of waiting — which is what produces the
        // bimodal paint cadence.
        const int n = 120;
        var inputs = MakeUniformTimestamps(n, 16.67);
        var encoded = EncodeAll(inputs);

        var perCallMs = new List<double>(encoded.Count);
        var sw = new Stopwatch();
        foreach (var ef in encoded)
        {
            sw.Restart();
            _decoder.Decode(ef.Bytes, ef.Timestamp);
            sw.Stop();
            perCallMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        // Skip the first 3 warmup calls — the decoder's MFT-negotiation
        // and SPS/PPS parse path is allowed to be slow on first contact.
        var steady = perCallMs.Skip(3).ToList();
        steady.Should().NotBeEmpty();

        var maxMs = steady.Max();
        var avgMs = steady.Average();
        var p95Ms = steady.OrderByDescending(x => x).Skip(steady.Count / 20).First();

        var diagnostic = $"avg={avgMs:F2}ms p95={p95Ms:F2}ms max={maxMs:F2}ms (samples={steady.Count})";

        // Hard limit: no decode should exceed the pacer prebuffer.
        maxMs.Should().BeLessThan(30.0,
            $"any decode call > 30ms prevents the pacer from hiding the latency spike — {diagnostic}");
        // Soft limit: the p95 should be comfortably inside the capture
        // frame gap (16.67 ms) so steady-state doesn't eat its own tail.
        p95Ms.Should().BeLessThan(16.67,
            $"95% of decode calls should complete within one capture frame gap — {diagnostic}");
    }

    [Fact]
    public void Encoder_per_call_latency_does_not_spike_above_pacer_prebuffer()
    {
        // Symmetric to the decoder version but for the encoder hot path.
        // The capture thread calls EncodeBgra/EncodeTexture once per WGC
        // frame; if any single call takes longer than the capture frame
        // gap, the next capture frame's scheduled arrival is pushed late
        // and the pacer sees a burst.
        const int n = 120;
        var inputs = MakeUniformTimestamps(n, 16.67);

        var bgra = new byte[Width * Height * 4];
        var stride = Width * 4;
        var perCallMs = new List<double>(inputs.Count);
        var sw = new Stopwatch();
        for (var i = 0; i < inputs.Count; i++)
        {
            FillFrame(bgra, frameIndex: i);
            sw.Restart();
            _encoder.EncodeBgra(bgra, stride, inputs[i]);
            sw.Stop();
            perCallMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        var steady = perCallMs.Skip(5).ToList();
        steady.Should().NotBeEmpty();

        var maxMs = steady.Max();
        var avgMs = steady.Average();
        var p95Ms = steady.OrderByDescending(x => x).Skip(steady.Count / 20).First();

        var diagnostic = $"avg={avgMs:F2}ms p95={p95Ms:F2}ms max={maxMs:F2}ms (samples={steady.Count})";

        maxMs.Should().BeLessThan(30.0,
            $"any encode call > 30ms blocks the WGC callback thread and starves the renderer's queue — {diagnostic}");
        p95Ms.Should().BeLessThan(16.67,
            $"95% of encode calls should complete within one capture frame gap — {diagnostic}");
    }

    [Fact]
    public void End_to_end_encode_decode_latency_variance_is_small()
    {
        // Measure the full per-frame pipeline: submit to encoder, drain
        // one output, submit to decoder, drain one output. This is what
        // the capture-test bridge does per WGC frame. If the variance
        // across calls is large, the renderer's queue sees bursty
        // arrivals even though PTS is correct — which is the most
        // plausible jitter-without-timestamp-errors hypothesis left.
        const int n = 120;
        var inputs = MakeUniformTimestamps(n, 16.67);

        var bgra = new byte[Width * Height * 4];
        var stride = Width * 4;
        var perFrameMs = new List<double>(inputs.Count);
        var sw = new Stopwatch();

        for (var i = 0; i < inputs.Count; i++)
        {
            FillFrame(bgra, frameIndex: i);
            sw.Restart();
            var ef = _encoder.EncodeBgra(bgra, stride, inputs[i]);
            if (ef is not null)
            {
                _decoder.Decode(ef.Value.Bytes, ef.Value.Timestamp);
            }
            sw.Stop();
            perFrameMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        var steady = perFrameMs.Skip(5).ToList();
        steady.Should().NotBeEmpty();

        var maxMs = steady.Max();
        var minMs = steady.Min();
        var avgMs = steady.Average();
        var spreadMs = maxMs - minMs;
        var p95Ms = steady.OrderByDescending(x => x).Skip(steady.Count / 20).First();

        var diagnostic = $"min={minMs:F2}ms avg={avgMs:F2}ms p95={p95Ms:F2}ms max={maxMs:F2}ms spread={spreadMs:F2}ms (samples={steady.Count})";

        // The important metric for smoothness is SPREAD, not the
        // absolute value. A stable 20ms latency is fine — the pacer
        // hides it behind the 30ms prebuffer. A 5-to-40ms bouncing
        // latency is NOT fine — it breaks the pacer's hide-latency
        // budget on every spike and creates the bimodal paint cadence.
        spreadMs.Should().BeLessThan(25.0,
            $"per-frame pipeline latency should be stable (spread < 25ms) so the pacer's prebuffer can hide it — {diagnostic}");
    }

    // -------------------------------------------------------------------
    // Content-vs-timestamp cross-check tests (the strongest kind).
    //
    // These tests paint every input frame with a unique solid colour
    // derived from its index. After the roundtrip, each decoded frame
    // carries BOTH a propagated timestamp and decoded pixel content.
    // We compute the index two independent ways:
    //   1. timestamp-derived index: look up decoded.Timestamp in a
    //      timestamp→input-index map built at encode time.
    //   2. content-derived index: average the centre 32×32 region of
    //      decoded.Bgra and find the input index whose ColorForIndex
    //      value is closest.
    // If the pipeline ever swaps image bytes between samples (e.g. two
    // frames with their contents exchanged but timestamps left alone,
    // or a single duplicated content body stamped with two different
    // neighbouring timestamps), these two indices disagree and the
    // test fails loudly pointing at exactly which frame was wrong.
    // -------------------------------------------------------------------

    [Fact]
    public void Roundtrip_content_and_timestamp_stay_aligned_under_uniform_60fps()
    {
        const double intervalMs = 16.67;
        const int n = 60;
        var inputs = MakeUniformTimestamps(n, intervalMs);
        var tsToIndex = BuildTimestampIndexMap(inputs);

        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        decoded.Should().NotBeEmpty();
        AssertContentMatchesTimestampIndex(decoded, tsToIndex, n);
    }

    [Fact]
    public void Roundtrip_content_and_timestamp_stay_aligned_under_alternating_14_21_ms()
    {
        const int n = 60;
        var inputs = MakeAlternatingTimestamps(n, 13.89, 20.83);
        var tsToIndex = BuildTimestampIndexMap(inputs);

        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        decoded.Should().NotBeEmpty();
        AssertContentMatchesTimestampIndex(decoded, tsToIndex, n);
    }

    [Fact]
    public void Roundtrip_no_two_decoded_frames_share_the_same_content()
    {
        // Tight duplicate-content detection: every decoded frame must be
        // visibly different from every other decoded frame. Solid colour
        // per index means "visibly different" == "content-derived index
        // is different". If the decoder ever releases two outputs whose
        // bytes come from the same input (e.g. a buffered frame replayed
        // alongside the next one), two outputs end up with the same
        // content-derived index and this test fails.
        const int n = 60;
        var inputs = MakeUniformTimestamps(n, 16.67);
        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        var contentIndices = decoded
            .Select(df => FindClosestIndexByColor(ReadCenterRegionAverage(df), n))
            .ToList();

        contentIndices.Should().OnlyHaveUniqueItems(
            "each decoded frame must carry content from a distinct input — duplicate content-derived indices mean two decoded outputs ended up with the same source frame's bytes");
    }

    private static Dictionary<TimeSpan, int> BuildTimestampIndexMap(IList<TimeSpan> inputs)
    {
        var map = new Dictionary<TimeSpan, int>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            map[inputs[i]] = i;
        }
        return map;
    }

    private static void AssertContentMatchesTimestampIndex(
        List<DecodedVideoFrame> decoded,
        Dictionary<TimeSpan, int> tsToIndex,
        int inputCount)
    {
        foreach (var df in decoded)
        {
            tsToIndex.TryGetValue(df.Timestamp, out var expectedIndex).Should().BeTrue(
                $"decoded timestamp {df.Timestamp.TotalMilliseconds:F2}ms must come from an input the test submitted");

            var avg = ReadCenterRegionAverage(df);
            var actualIndex = FindClosestIndexByColor(avg, inputCount);

            var expectedColor = ColorForIndex(expectedIndex);
            var actualColor = ColorForIndex(actualIndex);

            actualIndex.Should().Be(expectedIndex,
                $"decoded frame at {df.Timestamp.TotalMilliseconds:F2}ms has centre BGR=" +
                $"({avg.B},{avg.G},{avg.R}); the expected content for index {expectedIndex} is " +
                $"{expectedColor} but the closest match is index {actualIndex} with {actualColor}. " +
                $"This means the codec pair shuffled image bytes relative to sample timestamps — " +
                $"some input's content arrived wearing a different input's timestamp.");
        }
    }

    [Fact]
    public void Roundtrip_preserves_frame_dimensions()
    {
        var inputs = MakeUniformTimestamps(30, 16.67);
        var encoded = EncodeAll(inputs);
        var decoded = DecodeAll(encoded);

        decoded.Should().NotBeEmpty();
        foreach (var df in decoded)
        {
            df.Width.Should().Be(Width);
            df.Height.Should().Be(Height);
            df.Bgra.Should().NotBeNull();
            df.Bgra.Length.Should().BeGreaterOrEqualTo(Width * Height * 4);
        }
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private List<TimeSpan> MakeUniformTimestamps(int count, double intervalMs)
    {
        var list = new List<TimeSpan>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(TimeSpan.FromMilliseconds(i * intervalMs));
        }
        return list;
    }

    private List<TimeSpan> MakeAlternatingTimestamps(int count, double aMs, double bMs)
    {
        var list = new List<TimeSpan>(count);
        var t = 0.0;
        for (var i = 0; i < count; i++)
        {
            list.Add(TimeSpan.FromMilliseconds(t));
            t += i % 2 == 0 ? aMs : bMs;
        }
        return list;
    }

    private static IEnumerable<double> ComputeDeltasMs(IList<TimeSpan> ts)
    {
        for (var i = 1; i < ts.Count; i++)
        {
            yield return (ts[i] - ts[i - 1]).TotalMilliseconds;
        }
    }

    /// <summary>
    /// Submit a list of timestamped synthetic frames to the encoder and
    /// drain everything it produces. Each input frame gets a small
    /// per-frame mutation (a per-frame solid color shift) so NVENC
    /// doesn't compress it down to a zero-motion P-frame skip.
    /// </summary>
    private List<EncodedFrame> EncodeAll(IList<TimeSpan> timestamps)
    {
        var bgra = new byte[Width * Height * 4];
        var stride = Width * 4;
        var outputs = new List<EncodedFrame>();

        for (var i = 0; i < timestamps.Count; i++)
        {
            FillFrame(bgra, frameIndex: i);
            var ef = _encoder.EncodeBgra(bgra, stride, timestamps[i]);
            if (ef is not null) outputs.Add(ef.Value);
        }

        // Flush remaining outputs from the async pump. Submit a few
        // dummy frames with timestamps past the last real input so the
        // pump has reason to drain. We discard timestamps from these
        // dummies later (they're not in the original list).
        // 200 ms of small-sleep-and-poll lets the async pump catch up.
        var sw = Stopwatch.StartNew();
        var lastObservedCount = outputs.Count;
        var stableIters = 0;
        while (sw.ElapsedMilliseconds < 500 && stableIters < 4)
        {
            // Try a dummy submit (so any pending outputs get flushed)
            FillFrame(bgra, frameIndex: timestamps.Count + (int)sw.ElapsedMilliseconds);
            var trailing = _encoder.EncodeBgra(bgra, stride, TimeSpan.FromMilliseconds(timestamps.Last().TotalMilliseconds + sw.ElapsedMilliseconds + 1));
            if (trailing is not null) outputs.Add(trailing.Value);
            System.Threading.Thread.Sleep(5);
            if (outputs.Count == lastObservedCount) stableIters++;
            else { stableIters = 0; lastObservedCount = outputs.Count; }
        }

        // Strip any dummy-flush outputs whose timestamps aren't in the
        // original input list — caller only cares about the real ones.
        var inputSet = new HashSet<TimeSpan>(timestamps);
        return outputs.Where(o => inputSet.Contains(o.Timestamp)).ToList();
    }

    private List<DecodedVideoFrame> DecodeAll(IList<EncodedFrame> encoded)
    {
        var outputs = new List<DecodedVideoFrame>();
        foreach (var ef in encoded)
        {
            var frames = _decoder.Decode(ef.Bytes, ef.Timestamp);
            outputs.AddRange(frames);
        }
        // End-of-stream drain: the MF H.264 decoder keeps the tail of the
        // stream in its internal reorder buffer until more input arrives
        // or a drain message is sent. Without this, a finite 60-frame
        // test would see 20-ish trailing frames swallowed.
        outputs.AddRange(_decoder.Drain());
        return outputs;
    }

    /// <summary>
    /// Synthetic frame whose content is a per-index-unique solid BGRA
    /// color. The color is chosen by <see cref="ColorForIndex"/> using
    /// three distinct primes coprime with the period, so every frame in
    /// a 60-frame run has a colour that is both visibly distinct from
    /// every other frame AND far enough from its neighbours that a
    /// lossy-codec roundtrip cannot drift it into a neighbour's slot.
    /// This is what lets the roundtrip tests downstream read a decoded
    /// frame's pixels and derive *which input index* it originally came
    /// from — independently of its propagated timestamp.
    /// </summary>
    private static void FillFrame(byte[] bgra, int frameIndex)
    {
        var (b, g, r) = ColorForIndex(frameIndex);
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = (byte)b;
            bgra[i + 1] = (byte)g;
            bgra[i + 2] = (byte)r;
            bgra[i + 3] = 0xFF;
        }
    }

    /// <summary>
    /// Map a frame index to a BGRA colour triple. Values are clamped to
    /// [20, 220] so lossy BGRA→YUV→BGRA roundtrips don't clip a distinct
    /// colour to 0 or 255 (which would collapse multiple indices onto
    /// the same saturated value). The multipliers are distinct primes
    /// coprime with 200 so each channel is injective across any run of
    /// fewer than 200 frames, and consecutive indices end up far apart
    /// in colour space.
    /// </summary>
    private static (int B, int G, int R) ColorForIndex(int i)
    {
        return (
            20 + (Math.Abs(i) * 31) % 200,
            20 + (Math.Abs(i) * 61) % 200,
            20 + (Math.Abs(i) * 97) % 200);
    }

    /// <summary>
    /// Average BGRA values of a centred 32×32 region of a decoded frame.
    /// Centre sampling sidesteps the block-edge artefacts lossy H.264
    /// produces at macroblock boundaries, and averaging over 1024 pixels
    /// further smooths out single-pixel noise so the result is close to
    /// the original solid fill even with aggressive quantisation.
    /// </summary>
    private static (int B, int G, int R) ReadCenterRegionAverage(DecodedVideoFrame f)
    {
        const int region = 32;
        var x0 = Math.Max(0, (f.Width - region) / 2);
        var y0 = Math.Max(0, (f.Height - region) / 2);
        var x1 = Math.Min(f.Width, x0 + region);
        var y1 = Math.Min(f.Height, y0 + region);
        long sumB = 0, sumG = 0, sumR = 0, count = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = y * f.Width * 4;
            for (var x = x0; x < x1; x++)
            {
                var p = row + x * 4;
                sumB += f.Bgra[p + 0];
                sumG += f.Bgra[p + 1];
                sumR += f.Bgra[p + 2];
                count++;
            }
        }
        if (count == 0) return (0, 0, 0);
        return ((int)(sumB / count), (int)(sumG / count), (int)(sumR / count));
    }

    /// <summary>
    /// Given a decoded frame's averaged centre colour, return the index
    /// in [0..maxIndex) whose <see cref="ColorForIndex"/> value is closest
    /// by L1 distance. This is the inverse of <see cref="FillFrame"/> and
    /// lets a test answer "which input index does this decoded frame's
    /// CONTENT actually come from" independently of whatever timestamp
    /// the codec pair propagated through its SampleTime chain.
    /// </summary>
    private static int FindClosestIndexByColor((int B, int G, int R) avg, int maxIndex)
    {
        var bestIdx = -1;
        var bestDist = int.MaxValue;
        for (var i = 0; i < maxIndex; i++)
        {
            var (b, g, r) = ColorForIndex(i);
            var d = Math.Abs(avg.B - b) + Math.Abs(avg.G - g) + Math.Abs(avg.R - r);
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }
        return bestIdx;
    }
}
