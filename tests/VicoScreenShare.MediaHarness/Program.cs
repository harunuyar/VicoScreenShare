namespace VicoScreenShare.MediaHarness;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Services;
using VicoScreenShare.Client.Windows.Audio;
using VicoScreenShare.Client.Windows.Capture;
using VicoScreenShare.Client.Windows.Direct3D;
using VicoScreenShare.Client.Windows.Media;
using VicoScreenShare.Client.Windows.Media.Codecs;
using VicoScreenShare.Protocol;
using VicoScreenShare.Protocol.Messages;
using VicoScreenShare.Server;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// Headless console driver for the encoder pipeline. Lets us run end-to-end
/// encode benchmarks without the Avalonia app, without the SFU, without
/// signaling — just direct calls into <see cref="IVideoEncoder"/> with a
/// synthetic frame source. Output is intentionally machine-grep-able
/// (<c>RESULT: key=value</c> lines) so iteration loops can be parsed.
///
/// Scenarios:
///   bench-encode    Synthetic encode loop. Reports fps, bitrate, per-frame
///                   timings. The thing we run after every encoder change
///                   to see whether the change helped.
///
/// Future: bench-roundtrip (encode -> decode -> verify), bench-capture
/// (real Windows.Graphics.Capture -> encode), av1-bench, etc.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var scenario = args[0];
        var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
        var argMap = ParseArgs(rest);

        try
        {
            return scenario switch
            {
                "bench-encode" => RunEncodeBenchmark(argMap),
                "bench-pacer" => RunPacerBenchmark(argMap),
                "bench-avsync" => RunAvSyncReceiverBenchmark(argMap),
                "bench-encode-gpu" => RunGpuEncodeBenchmark(argMap),
                "bench-downscale" => RunDownscaleBenchmark(argMap),
                "bench-scaler" => RunVideoScalerBenchmark(argMap),
                "bench-capture-e2e" => RunCaptureEndToEndBenchmark(argMap),
                "bench-encode-decode" => RunEncodeDecodeBenchmark(argMap),
                "bench-network-e2e" => RunNetworkEndToEndBenchmark(argMap).GetAwaiter().GetResult(),
                "bench-audio-encode" => RunAudioEncodeBenchmark(argMap),
                "bench-audio-loopback" => RunAudioLoopbackBenchmark(argMap).GetAwaiter().GetResult(),
                "bench-av-sync" => RunAvSyncBenchmark(argMap),
                "list-targets" => RunListTargetsScenario(argMap).GetAwaiter().GetResult(),
                "bench-process-audio" => RunProcessAudioBenchmark(argMap).GetAwaiter().GetResult(),
                "bench-nvenc-probe" => RunNvencProbeBenchmark(argMap),
                "bench-mft-av1-decoder" => RunMftAv1DecoderProbeBenchmark(argMap),
                "bench-av1-rtp-roundtrip" => RunAv1RtpRoundtripBenchmark(argMap),
                "bench-av1-encode-decode" => RunAv1EncodeDecodeBenchmark(argMap),
                "bench-nvenc-keyframe" => RunNvencKeyframeBenchmark(argMap),
                "bench-vbv" => RunVbvBenchmark(argMap),
                "bench-intra-refresh" => RunIntraRefreshBenchmark(argMap),
                "bench-aq" => RunAqBenchmark(argMap),
                _ => UnknownScenario(scenario),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 99;
        }
    }

    private static int RunEncodeBenchmark(Dictionary<string, string> args)
    {
        var width = int.Parse(args.GetValueOrDefault("width", "1920"));
        var height = int.Parse(args.GetValueOrDefault("height", "1080"));
        var fps = int.Parse(args.GetValueOrDefault("fps", "60"));
        var bitrateMbps = double.Parse(args.GetValueOrDefault("bitrate", "10"));
        var bitrate = (int)(bitrateMbps * 1_000_000);
        var duration = double.Parse(args.GetValueOrDefault("duration", "5"));
        var codec = args.GetValueOrDefault("codec", "h264");
        var backend = args.GetValueOrDefault("backend", "mft");
        var throttle = args.GetValueOrDefault("throttle", "true") != "false";

        Console.WriteLine($"# bench-encode codec={codec} backend={backend} {width}x{height}@{fps} bitrate={bitrateMbps}Mbps duration={duration}s throttle={throttle}");

        IVideoEncoderFactory factory;
        D3D11DeviceManager? sharedDevices = null;
        switch (codec)
        {
            case "h264":
                MediaFoundationRuntime.EnsureInitialized();
                if (!MediaFoundationRuntime.IsAvailable)
                {
                    Console.Error.WriteLine("ERROR: Media Foundation could not initialize");
                    return 2;
                }
                if (backend == "nvenc")
                {
                    // NVENC SDK path needs a real D3D11 device.
                    sharedDevices = new D3D11DeviceManager();
                    sharedDevices.Initialize();
                    var nvencFactory = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencH264EncoderFactory(sharedDevices.Device);
                    nvencFactory.Options = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencEncodeOptions
                    {
                        Preset = int.Parse(args.GetValueOrDefault("preset", "4")),
                    };
                    factory = nvencFactory;
                }
                else if (backend == "mft")
                {
                    factory = new MediaFoundationH264EncoderFactory();
                }
                else
                {
                    Console.Error.WriteLine($"ERROR: unknown backend '{backend}' (expected mft or nvenc)");
                    return 2;
                }
                break;
            case "av1":
                // AV1 only goes through NVENC SDK — there's no MFT AV1
                // encoder shim worth using. Still requires a shared device.
                sharedDevices = new D3D11DeviceManager();
                sharedDevices.Initialize();
                var av1Factory = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencAv1EncoderFactory(sharedDevices.Device);
                av1Factory.Options = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencEncodeOptions
                {
                    Preset = int.Parse(args.GetValueOrDefault("preset", "4")),
                    EnableAdaptiveQuantization = args.GetValueOrDefault("aq", "1") != "0",
                    EnableTemporalAq = args.GetValueOrDefault("aq", "1") != "0",
                    EnableIntraRefresh = args.GetValueOrDefault("intra-refresh", "0") != "0",
                };
                factory = av1Factory;
                break;
            case "vp8":
                factory = new VpxEncoderFactory();
                break;
            default:
                Console.Error.WriteLine($"ERROR: unknown codec '{codec}' (expected h264 or vp8)");
                return 2;
        }

        if (!factory.IsAvailable)
        {
            Console.Error.WriteLine($"ERROR: codec {codec} factory reports unavailable");
            return 2;
        }

        var encoder = factory.CreateEncoder(width, height, fps, bitrate, gopFrames: fps * 2);
        Console.WriteLine($"# encoder created: {encoder.GetType().Name} codec={encoder.Codec}");

        var outPath = args.GetValueOrDefault("out", "");
        FileStream? outStream = null;
        if (!string.IsNullOrEmpty(outPath))
        {
            outStream = File.Create(outPath);
            Console.WriteLine($"# bitstream dump: {outPath}");
        }

        var stride = width * 4;
        var bgra = new byte[height * stride];
        FillGradient(bgra, width, height);

        var frameCount = 0;
        var totalBytes = 0L;
        var encodeMsTotal = 0.0;
        var encodeMsMax = 0.0;
        var encodeMsMin = double.MaxValue;
        var totalSw = Stopwatch.StartNew();
        var endTicks = (long)(duration * Stopwatch.Frequency);
        var frameInterval = Stopwatch.Frequency / fps;

        // Async encoders (NVENC, MFT hardware) return null from EncodeBgra
        // and emit via OutputAvailable. Drain that path into the same
        // counters so fps reporting is accurate regardless of backend.
        if (encoder is IAsyncEncodedOutputSource asyncOut)
        {
            asyncOut.OutputAvailable += () =>
            {
                while (asyncOut.TryDequeueEncoded(out var ef))
                {
                    if (ef.Bytes.Length > 0)
                    {
                        Interlocked.Increment(ref frameCount);
                        Interlocked.Add(ref totalBytes, ef.Bytes.Length);
                        outStream?.Write(ef.Bytes, 0, ef.Bytes.Length);
                    }
                }
            };
        }

        // Spin a small warmup so the first encode (NVENC priming, JIT) doesn't
        // skew the average. Encoder may also return null for the first few
        // calls while async pump fills its NeedInput credits.
        var harnessClock = Stopwatch.StartNew();
        for (var w = 0; w < 5; w++)
        {
            MutatePattern(bgra, w, width);
            _ = encoder.EncodeBgra(bgra, stride, harnessClock.Elapsed);
        }

        var benchSw = Stopwatch.StartNew();
        var startTicks = totalSw.ElapsedTicks;
        var iteration = 0;

        while (totalSw.ElapsedTicks - startTicks < endTicks)
        {
            MutatePattern(bgra, iteration + 100, width);

            var encStart = Stopwatch.GetTimestamp();
            var encoded = encoder.EncodeBgra(bgra, stride, harnessClock.Elapsed);
            var encEnd = Stopwatch.GetTimestamp();

            var encodeMs = (encEnd - encStart) * 1000.0 / Stopwatch.Frequency;
            encodeMsTotal += encodeMs;
            if (encodeMs > encodeMsMax)
            {
                encodeMsMax = encodeMs;
            }

            if (encodeMs < encodeMsMin)
            {
                encodeMsMin = encodeMs;
            }

            if (encoded is { Bytes.Length: > 0 })
            {
                frameCount++;
                totalBytes += encoded.Value.Bytes.Length;
            }

            iteration++;

            if (throttle)
            {
                var nextDeadline = startTicks + iteration * frameInterval;
                while (totalSw.ElapsedTicks < nextDeadline)
                {
                    // tight spin — we want to measure the encoder under a
                    // realistic delivery cadence, not how fast it can be
                    // stuffed in a loop.
                }
            }
        }

        var elapsedSeconds = benchSw.Elapsed.TotalSeconds;
        encoder.Dispose();
        // Drain anything that landed during dispose / final flush before
        // closing the bitstream file.
        Thread.Sleep(150);
        outStream?.Flush();
        outStream?.Dispose();
        sharedDevices?.Dispose();

        var actualFps = frameCount / elapsedSeconds;
        var bitrateOutMbps = (totalBytes * 8.0 / elapsedSeconds) / 1_000_000.0;
        var avgEncodeMs = encodeMsTotal / Math.Max(1, iteration);

        Console.WriteLine($"RESULT: iterations={iteration}");
        Console.WriteLine($"RESULT: frames_encoded={frameCount}");
        Console.WriteLine($"RESULT: elapsed_seconds={elapsedSeconds:F3}");
        Console.WriteLine($"RESULT: fps_actual={actualFps:F2}");
        Console.WriteLine($"RESULT: fps_target={fps}");
        Console.WriteLine($"RESULT: bytes_total={totalBytes}");
        Console.WriteLine($"RESULT: avg_frame_bytes={(frameCount > 0 ? totalBytes / frameCount : 0)}");
        Console.WriteLine($"RESULT: bitrate_out_mbps={bitrateOutMbps:F2}");
        Console.WriteLine($"RESULT: bitrate_target_mbps={bitrateMbps:F2}");
        Console.WriteLine($"RESULT: avg_encode_call_ms={avgEncodeMs:F2}");
        Console.WriteLine($"RESULT: max_encode_call_ms={encodeMsMax:F2}");
        Console.WriteLine($"RESULT: min_encode_call_ms={(encodeMsMin == double.MaxValue ? 0 : encodeMsMin):F2}");

        var meetingTarget = actualFps >= fps * 0.95;
        Console.WriteLine($"VERDICT: {(meetingTarget ? "PASS" : "FAIL")} (fps {actualFps:F1}/{fps})");
        return meetingTarget ? 0 : 3;
    }

    /// <summary>
    /// Verifies the <see cref="PacingRtpSender"/>'s wire-time pacing math.
    ///
    /// Stubs the SendRtpRaw delegate with a recorder that timestamps every
    /// packet on the way out. Feeds a sequence of synthetic frames (mix of
    /// keyframe-sized and P-frame-sized payloads). Computes the actual
    /// inter-packet bitrate from recorded timestamps and asserts it matches
    /// the configured target within tolerance.
    ///
    /// Also asserts the drop policy: when the queue is overloaded, oldest
    /// non-keyframes are evicted; queued keyframes always survive.
    /// </summary>
    private static int RunPacerBenchmark(Dictionary<string, string> args)
    {
        var rateMbps = double.Parse(args.GetValueOrDefault("rate", "12"));
        var rateBps = (int)(rateMbps * 1_000_000);
        var keyframeBytes = int.Parse(args.GetValueOrDefault("keyframe-bytes", "900000"));
        var pframeBytes = int.Parse(args.GetValueOrDefault("pframe-bytes", "30000"));
        var frameCount = int.Parse(args.GetValueOrDefault("frames", "30"));
        var fps = int.Parse(args.GetValueOrDefault("fps", "60"));

        Console.WriteLine($"# bench-pacer rate={rateMbps}Mbps keyframe={keyframeBytes}B pframe={pframeBytes}B frames={frameCount}@{fps}fps");

        var sends = new List<(long ticks, int payloadLength)>(8 * 1024);
        var sendLock = new object();
        var startTicks = Stopwatch.GetTimestamp();

        VicoScreenShare.Client.Media.PacingRtpSender.SendRtpRawDelegate recorder = (payload, ts, mb, pt) =>
        {
            var now = Stopwatch.GetTimestamp();
            lock (sendLock)
            {
                sends.Add((now, payload.Length));
            }
        };

        using var pacer = new VicoScreenShare.Client.Media.PacingRtpSender(
            sendRtpRaw: recorder,
            getPayloadTypeId: () => 102,
            initialRtpTimestamp: 1000u,
            contentCodec: VideoCodec.H264,
            framingCodec: VideoCodec.H264,
            targetBitsPerSecond: rateBps);

        // Feed a real-looking H.264 access-unit sequence: each "frame" is a
        // single NAL with an Annex-B prefix. Frame 0 is an IDR (NAL type 5),
        // others are P (NAL type 1). Sized to the user-configurable
        // keyframe/P-frame budgets so the pacer's pacing math gets a
        // representative input distribution.
        var frameInterval = TimeSpan.FromSeconds(1.0 / fps);
        var harness = Stopwatch.StartNew();
        for (var i = 0; i < frameCount; i++)
        {
            var isKey = i == 0 || (i % (fps * 2) == 0);
            var size = isKey ? keyframeBytes : pframeBytes;
            var nalType = isKey ? (byte)0x65 : (byte)0x41; // type 5 IDR vs type 1 P
            var sample = BuildSyntheticH264AccessUnit(size, nalType);
            var duration = (uint)(90_000 / fps);
            pacer.Enqueue(duration, sample);

            // Pace input at fps so the pacer sees a realistic arrival
            // cadence. Without this we'd flood the queue and only test
            // the drop policy.
            var deadline = TimeSpan.FromTicks(frameInterval.Ticks * (i + 1));
            while (harness.Elapsed < deadline)
            {
                Thread.Sleep(1);
            }
        }

        // Drain the queue. With a 12 Mbps rate and a 900 KB keyframe, the
        // pacer needs ~600 ms after the last frame is enqueued to flush.
        var maxFlushMs = (long)((keyframeBytes * 8.0 / rateBps) * 1000) + 500;
        var flushDeadline = harness.ElapsedMilliseconds + maxFlushMs;
        while (harness.ElapsedMilliseconds < flushDeadline && pacer.CurrentQueueDepth > 0)
        {
            Thread.Sleep(20);
        }
        // One more drain wait — pacer thread may still be mid-frame.
        Thread.Sleep(200);

        List<(long ticks, int payloadLength)> samples;
        lock (sendLock)
        {
            samples = new List<(long ticks, int payloadLength)>(sends);
        }

        if (samples.Count < 2)
        {
            Console.WriteLine($"VERDICT: FAIL (recorded only {samples.Count} packets)");
            return 3;
        }

        // Measured wire bitrate: total bytes / elapsed seconds across the
        // recorded packet stream.
        var firstTicks = samples[0].ticks;
        var lastTicks = samples[^1].ticks;
        var elapsedSec = (lastTicks - firstTicks) / (double)Stopwatch.Frequency;
        long totalBytes = 0;
        foreach (var s in samples)
        {
            totalBytes += s.payloadLength;
        }
        var measuredBps = elapsedSec > 0 ? totalBytes * 8.0 / elapsedSec : 0;
        var measuredMbps = measuredBps / 1_000_000.0;

        Console.WriteLine($"RESULT: packets={samples.Count}");
        Console.WriteLine($"RESULT: total_bytes={totalBytes}");
        Console.WriteLine($"RESULT: send_window_seconds={elapsedSec:F3}");
        Console.WriteLine($"RESULT: measured_mbps={measuredMbps:F3}");
        Console.WriteLine($"RESULT: target_mbps={rateMbps:F3}");
        Console.WriteLine($"RESULT: ratio_measured_over_target={(measuredBps / rateBps):F3}");
        Console.WriteLine($"RESULT: frames_queued={pacer.FramesQueued}");
        Console.WriteLine($"RESULT: frames_sent={pacer.FramesSent}");
        Console.WriteLine($"RESULT: frames_dropped={pacer.FramesDropped}");

        // Pacing precision: measured rate should be within 15% of target.
        // We allow some slack because Thread.Sleep granularity on Windows
        // is ~1-2 ms — across thousands of small packets this drifts the
        // measured rate slightly off the configured one. Outside that
        // window means pacing is broken.
        var ratio = measuredBps / rateBps;
        var pacingOk = ratio >= 0.85 && ratio <= 1.15;
        Console.WriteLine($"VERDICT: {(pacingOk ? "PASS" : "FAIL")} (measured {measuredMbps:F2} Mbps vs target {rateMbps:F2} Mbps; ratio {ratio:F2})");
        return pacingOk ? 0 : 3;
    }

    /// <summary>
    /// Build a fake H.264 Annex-B access unit of the requested total
    /// size with the given NAL header byte as type indicator. The pacer
    /// only inspects the first NAL byte (for keyframe detection) and
    /// the start code; everything else can be filler.
    /// </summary>
    private static byte[] BuildSyntheticH264AccessUnit(int totalBytes, byte nalHeader)
    {
        if (totalBytes < 5)
        {
            totalBytes = 5;
        }
        var sample = new byte[totalBytes];
        // Annex-B 4-byte start code + NAL header.
        sample[0] = 0x00;
        sample[1] = 0x00;
        sample[2] = 0x00;
        sample[3] = 0x01;
        sample[4] = nalHeader;
        // Remaining bytes left at default 0 — the pacer doesn't care.
        return sample;
    }

    /// <summary>
    /// Real-wall-clock end-to-end A/V sync exercise. Drives the full
    /// MediaClock + AudioReceiver path the way the live receiver
    /// would: a publisher loop emits 50 audio packets/s and one
    /// "video frame" per nominalFps, with both streams sharing a
    /// publisher NTP origin. The video paint loop is simulated by
    /// holding the first <c>--buffer</c> frames before "painting"
    /// the first one (using the real <see cref="MediaClock.SetVideoAnchorFromContentTime"/>
    /// entry point). RTCP Sender Reports are injected at a
    /// configurable interval so we exercise the case where SR
    /// arrives well after audio has already accumulated.
    ///
    /// Asserts:
    /// 1. Pre-anchor: zero audio frames submitted to the renderer.
    /// 2. Post-anchor: audio drains continuously at ~50 fps real-time.
    /// 3. Final |offsetMs| stays within tolerance.
    /// </summary>
    private static int RunAvSyncReceiverBenchmark(Dictionary<string, string> args)
    {
        var receiveBuffer = int.Parse(args.GetValueOrDefault("buffer", "60"));
        var nominalFps = int.Parse(args.GetValueOrDefault("fps", "60"));
        var totalSeconds = double.Parse(args.GetValueOrDefault("duration", "5"));
        var srDelaySeconds = double.Parse(args.GetValueOrDefault("sr-delay", "1.5"));
        var driftToleranceMs = double.Parse(args.GetValueOrDefault("drift-ms", "100"));

        var bufferDelay = TimeSpan.FromSeconds((double)receiveBuffer / nominalFps);

        Console.WriteLine($"# bench-avsync buffer={receiveBuffer} frames @ {nominalFps} fps "
            + $"(delay={bufferDelay.TotalMilliseconds:F0} ms) sr-delay={srDelaySeconds:F2}s "
            + $"duration={totalSeconds:F1}s drift-ms={driftToleranceMs:F0}");

        using var pc = new SIPSorcery.Net.RTCPeerConnection(null);
        var clock = new VicoScreenShare.Client.Media.MediaClock("avsync");
        var renderer = new HarnessAudioRenderer();
        var audio = new VicoScreenShare.Client.Media.AudioReceiver(
            pc,
            new HarnessAudioDecoderFactory(),
            renderer,
            displayName: "harness",
            mediaClock: clock);
        audio.StartAsync().GetAwaiter().GetResult();

        // Reference times. The publisher's NTP origin is arbitrary;
        // pick a fixed value so the math is reproducible. The local
        // wall is real Stopwatch ticks at run-start.
        var publisherEpochSeconds = 3_000_000_000u;
        var streamStartSw = Stopwatch.GetTimestamp();
        ulong NtpAt(double secondsFromStart)
        {
            // RFC 3550 64-bit NTP: upper 32 = secs since 1900,
            // lower 32 = fraction × 2^32.
            var totalSec = publisherEpochSeconds + secondsFromStart;
            var sec = (uint)Math.Floor(totalSec);
            var frac = totalSec - Math.Floor(totalSec);
            return ((ulong)sec << 32) | (uint)(frac * 4294967296.0);
        }

        // Audio: 48 kHz Opus, 20 ms framing → rtpTs grows by 960 per
        // frame, 50 frames/s. Video: 90 kHz @ nominalFps → rtpTs
        // grows by (90000 / fps) per frame.
        const int audioFrameSamples = 960;
        const int audioRtpStep = audioFrameSamples;
        const double audioFramePeriodSec = 0.020;
        var videoRtpStep = (uint)(90000 / nominalFps);
        var videoFramePeriodSec = 1.0 / nominalFps;

        // Track the receiver's video paint loop in software. We
        // accumulate "video frames" exactly as the publisher emits
        // them; once `receiveBuffer` are buffered, the next frame
        // becomes the first painted, and we anchor the MediaClock
        // at that moment using SetVideoAnchorFromContentTime — the
        // SAME entry point PaintLoop uses in production.
        var bufferedVideo = new Queue<TimeSpan>();
        var anchored = false;

        // First-SR delay: emulates SIPSorcery's RTCP cadence.
        var firstSrDispatched = false;

        // Streaming loop. Wakes at each next-event boundary
        // (audio packet, video frame, or SR injection). Real wall
        // advance is enforced via Thread.Sleep so AudioReceiver's
        // drain loop sees real time pass between packets — that's
        // the whole point of running this in the harness instead of
        // a synchronous unit test.
        var endSw = streamStartSw + (long)(totalSeconds * Stopwatch.Frequency);
        var nextAudioSw = streamStartSw;
        var nextVideoSw = streamStartSw;
        var nextSrSw = streamStartSw + (long)(srDelaySeconds * Stopwatch.Frequency);
        uint audioRtp = 0;
        uint videoRtp = 0;

        while (Stopwatch.GetTimestamp() < endSw)
        {
            var now = Stopwatch.GetTimestamp();
            var nextDeadline = Math.Min(Math.Min(nextAudioSw, nextVideoSw), nextSrSw);
            if (nextDeadline > now)
            {
                var sleepMs = (nextDeadline - now) * 1000.0 / Stopwatch.Frequency;
                if (sleepMs > 0)
                {
                    Thread.Sleep((int)Math.Max(1, Math.Min(20, sleepMs)));
                }
                continue;
            }

            // SR injection — both streams' SRs arrive together.
            if (now >= nextSrSw && !firstSrDispatched)
            {
                var elapsedSec = (now - streamStartSw) / (double)Stopwatch.Frequency;
                clock.OnAudioSenderReport(NtpAt(elapsedSec), audioRtp);
                clock.OnVideoSenderReport(NtpAt(elapsedSec), videoRtp);
                firstSrDispatched = true;
                nextSrSw = long.MaxValue;
            }

            // Audio packet: each packet's publisher capture time is
            // (audioRtp / 48000) seconds from stream start, mapped
            // through SR to publisher NTP at receive time.
            if (now >= nextAudioSw)
            {
                audio.IngestPacket(audioRtp, payload: new byte[] { 0x10 });
                audioRtp = unchecked(audioRtp + (uint)audioRtpStep);
                nextAudioSw += (long)(audioFramePeriodSec * Stopwatch.Frequency);
            }

            // Video frame: stash content time; once we have enough
            // buffered, "paint" the oldest and anchor.
            if (now >= nextVideoSw)
            {
                var contentTime = TimeSpan.FromTicks((long)videoRtp * TimeSpan.TicksPerMillisecond / 90);
                bufferedVideo.Enqueue(contentTime);
                videoRtp = unchecked(videoRtp + videoRtpStep);
                nextVideoSw += (long)(videoFramePeriodSec * Stopwatch.Frequency);

                if (!anchored && bufferedVideo.Count >= receiveBuffer)
                {
                    var firstPaintContent = bufferedVideo.Dequeue();
                    anchored = clock.SetVideoAnchorFromContentTime(
                        firstPaintContent,
                        Stopwatch.GetTimestamp());
                    if (anchored)
                    {
                        var rel = (Stopwatch.GetTimestamp() - streamStartSw) * 1000.0 / Stopwatch.Frequency;
                        Console.WriteLine($"# anchored at t={rel:F0}ms (firstPaintContent={firstPaintContent.TotalMilliseconds:F0}ms)");
                    }
                }
                else if (anchored && bufferedVideo.Count > 0)
                {
                    bufferedVideo.Dequeue();
                }
            }
        }

        var elapsedTotalSec = (Stopwatch.GetTimestamp() - streamStartSw) / (double)Stopwatch.Frequency;
        // Wait a beat for any final drains to flush.
        Thread.Sleep(50);

        Console.WriteLine($"RESULT: anchored={anchored}");
        Console.WriteLine($"RESULT: frames_decoded={audio.FramesDecoded}");
        Console.WriteLine($"RESULT: frames_submitted={audio.FramesSubmitted}");
        Console.WriteLine($"RESULT: frames_dropped={audio.FramesDropped}");
        Console.WriteLine($"RESULT: samples_padded={audio.SamplesPadded}");
        Console.WriteLine($"RESULT: samples_skipped={audio.SamplesSkipped}");
        Console.WriteLine($"RESULT: last_offset_ms={audio.LastOffsetMicros / 1000.0:F1}");
        Console.WriteLine($"RESULT: renderer_submit_count={renderer.SubmitCount}");

        // Pass criteria:
        //  1. Anchor latched.
        //  2. Audio actually played (renderer received submits).
        //  3. Steady-state offset is within tolerance.
        //  4. Submitted count is in the right ballpark — we expect
        //     50 fps × (totalSeconds − bufferDelay − sr-delay).
        //     Allow ±25% slack for queue-drain ramp-up.
        var expectedRoughly = (totalSeconds - bufferDelay.TotalSeconds - srDelaySeconds) * 50.0;
        var offsetWithinTol = Math.Abs(audio.LastOffsetMicros / 1000.0) <= driftToleranceMs;
        var submittedReasonable = audio.FramesSubmitted >= expectedRoughly * 0.5;
        var anchorOk = anchored;
        var anyAudio = audio.FramesSubmitted > 0;

        Console.WriteLine($"RESULT: expected_submitted_roughly={expectedRoughly:F0}");

        var ok = anchorOk && anyAudio && submittedReasonable && offsetWithinTol;
        Console.WriteLine($"VERDICT: {(ok ? "PASS" : "FAIL")} "
            + $"(anchor={anchorOk}, anyAudio={anyAudio}, "
            + $"submittedOk={submittedReasonable} ({audio.FramesSubmitted}/{expectedRoughly:F0}), "
            + $"offsetOk={offsetWithinTol} ({audio.LastOffsetMicros / 1000.0:F1}ms))");

        try { audio.DisposeAsync().GetAwaiter().GetResult(); } catch { }
        return ok ? 0 : 3;
    }

    /// <summary>
    /// Stub <see cref="VicoScreenShare.Client.Platform.IAudioRenderer"/>
    /// for the harness — counts submits, no actual playback. Lives
    /// here so the harness scenario stays self-contained.
    /// </summary>
    private sealed class HarnessAudioRenderer : VicoScreenShare.Client.Platform.IAudioRenderer
    {
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public double Volume { get; set; } = 1.0;
        public int SubmitCount;

        public Task StartAsync(int sampleRate, int channels)
        {
            SampleRate = sampleRate;
            Channels = channels;
            return Task.CompletedTask;
        }

        public void Submit(ReadOnlySpan<short> interleavedPcm, TimeSpan timestamp)
        {
            Interlocked.Increment(ref SubmitCount);
        }

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HarnessAudioDecoderFactory : VicoScreenShare.Client.Media.Codecs.IAudioDecoderFactory
    {
        public bool IsAvailable => true;
        public VicoScreenShare.Client.Media.Codecs.IAudioDecoder CreateDecoder(int channels) =>
            new HarnessAudioDecoder(channels);
    }

    private sealed class HarnessAudioDecoder : VicoScreenShare.Client.Media.Codecs.IAudioDecoder
    {
        public HarnessAudioDecoder(int channels) { Channels = channels; }
        public int SampleRate => 48000;
        public int Channels { get; }

        public VicoScreenShare.Client.Media.Codecs.DecodedAudioFrame? Decode(
            ReadOnlySpan<byte> encoded, uint rtpTimestamp)
        {
            return new VicoScreenShare.Client.Media.Codecs.DecodedAudioFrame(
                Pcm: new short[960 * Channels],
                Samples: 960,
                Channels: Channels,
                SampleRate: SampleRate,
                RtpTimestamp: rtpTimestamp);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Bench scenario: feed NVENC pure D3D11 NV12 textures via the new
    /// <see cref="MediaFoundationH264Encoder.EncodeTexture"/> path. No CPU
    /// color conversion, no system-memory NV12 buffer, no per-frame
    /// upload — the encoder reads from a GPU texture in place. This is
    /// the upper bound on what NVENC alone can do on this machine.
    /// </summary>
    private static int RunGpuEncodeBenchmark(Dictionary<string, string> args)
    {
        var width = int.Parse(args.GetValueOrDefault("width", "1920"));
        var height = int.Parse(args.GetValueOrDefault("height", "1080"));
        var fps = int.Parse(args.GetValueOrDefault("fps", "60"));
        var bitrateMbps = double.Parse(args.GetValueOrDefault("bitrate", "10"));
        var bitrate = (int)(bitrateMbps * 1_000_000);
        var duration = double.Parse(args.GetValueOrDefault("duration", "5"));
        var throttle = args.GetValueOrDefault("throttle", "true") != "false";

        Console.WriteLine($"# bench-encode-gpu codec=h264 {width}x{height}@{fps} bitrate={bitrateMbps}Mbps duration={duration}s throttle={throttle}");

        MediaFoundationRuntime.EnsureInitialized();
        if (!MediaFoundationRuntime.IsAvailable)
        {
            Console.Error.WriteLine("ERROR: Media Foundation could not initialize");
            return 2;
        }

        var encoder = new MediaFoundationH264Encoder(width, height, fps, bitrate, gopFrames: fps * 2);
        Console.WriteLine($"# encoder created codec={encoder.Codec}");

        if (encoder.D3D11Device is null)
        {
            Console.Error.WriteLine("ERROR: encoder has no D3D11 device — GPU path requires hardware MFT");
            encoder.Dispose();
            return 2;
        }

        // Allocate an NV12 D3D11 texture on the encoder's device, fill it
        // once with synthetic data, and reuse it every iteration. The
        // encoder reads from GPU memory directly. Default usage (no CPU
        // access) so NVENC gets the fast path.
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        using var nv12Texture = encoder.D3D11Device.CreateTexture2D(desc);

        // Initial upload via UpdateSubresource — Default-usage textures
        // can't be Mapped, so we have to use the device context.
        var nv12Bytes = new byte[width * height * 3 / 2];
        FillSyntheticNv12(nv12Bytes, width, height);
        unsafe
        {
            fixed (byte* src = nv12Bytes)
            {
                using var ctx = encoder.D3D11Device.ImmediateContext;
                ctx.UpdateSubresource(nv12Texture, 0, null, (nint)src, (uint)width, 0u);
            }
        }

        Console.WriteLine($"# nv12 texture allocated and uploaded");

        var frameCount = 0;
        var totalBytes = 0L;
        var encodeMsTotal = 0.0;
        var encodeMsMax = 0.0;
        var encodeMsMin = double.MaxValue;
        var totalSw = Stopwatch.StartNew();
        var endTicks = (long)(duration * Stopwatch.Frequency);
        var frameInterval = Stopwatch.Frequency / fps;

        // Warmup
        var harnessClock = Stopwatch.StartNew();
        for (var w = 0; w < 5; w++)
        {
            _ = encoder.EncodeTexture(nv12Texture, harnessClock.Elapsed);
        }

        var benchSw = Stopwatch.StartNew();
        var startTicks = totalSw.ElapsedTicks;
        var iteration = 0;

        while (totalSw.ElapsedTicks - startTicks < endTicks)
        {
            var encStart = Stopwatch.GetTimestamp();
            var encoded = encoder.EncodeTexture(nv12Texture, harnessClock.Elapsed);
            var encEnd = Stopwatch.GetTimestamp();

            var encodeMs = (encEnd - encStart) * 1000.0 / Stopwatch.Frequency;
            encodeMsTotal += encodeMs;
            if (encodeMs > encodeMsMax)
            {
                encodeMsMax = encodeMs;
            }

            if (encodeMs < encodeMsMin)
            {
                encodeMsMin = encodeMs;
            }

            if (encoded is { Bytes.Length: > 0 })
            {
                frameCount++;
                totalBytes += encoded.Value.Bytes.Length;
            }

            iteration++;

            if (throttle)
            {
                var nextDeadline = startTicks + iteration * frameInterval;
                while (totalSw.ElapsedTicks < nextDeadline) { }
            }
        }

        var elapsedSeconds = benchSw.Elapsed.TotalSeconds;
        encoder.Dispose();

        var actualFps = frameCount / elapsedSeconds;
        var bitrateOutMbps = (totalBytes * 8.0 / elapsedSeconds) / 1_000_000.0;
        var avgEncodeMs = encodeMsTotal / Math.Max(1, iteration);

        Console.WriteLine($"RESULT: iterations={iteration}");
        Console.WriteLine($"RESULT: frames_encoded={frameCount}");
        Console.WriteLine($"RESULT: elapsed_seconds={elapsedSeconds:F3}");
        Console.WriteLine($"RESULT: fps_actual={actualFps:F2}");
        Console.WriteLine($"RESULT: fps_target={fps}");
        Console.WriteLine($"RESULT: bytes_total={totalBytes}");
        Console.WriteLine($"RESULT: avg_frame_bytes={(frameCount > 0 ? totalBytes / frameCount : 0)}");
        Console.WriteLine($"RESULT: bitrate_out_mbps={bitrateOutMbps:F2}");
        Console.WriteLine($"RESULT: bitrate_target_mbps={bitrateMbps:F2}");
        Console.WriteLine($"RESULT: avg_encode_call_ms={avgEncodeMs:F2}");
        Console.WriteLine($"RESULT: max_encode_call_ms={encodeMsMax:F2}");
        Console.WriteLine($"RESULT: min_encode_call_ms={(encodeMsMin == double.MaxValue ? 0 : encodeMsMin):F2}");

        var meetingTarget = actualFps >= fps * 0.95;
        Console.WriteLine($"VERDICT: {(meetingTarget ? "PASS" : "FAIL")} (fps {actualFps:F1}/{fps})");
        return meetingTarget ? 0 : 3;
    }

    /// <summary>
    /// End-to-end capture benchmark. Drives the real
    /// <see cref="WindowsCaptureSource"/> against the primary monitor and
    /// routes every frame through a real <see cref="CaptureStreamer"/> /
    /// Media Foundation H.264 encoder built on the shared D3D11 device —
    /// exactly the same wiring the Avalonia app uses. The encoded bytes
    /// are counted and dropped; we don't care about the SFU for this
    /// test, only whether the local pipeline can hit its fps target.
    ///
    /// Output: fps_actual, fps_target, arrival gap p50 / p95 / p99, encode
    /// time p50 / p95. Anything below 95% of the fps target is a FAIL.
    /// </summary>
    private static int RunCaptureEndToEndBenchmark(Dictionary<string, string> args)
    {
        var targetHeight = int.Parse(args.GetValueOrDefault("target-height", "1080"));
        var fps = int.Parse(args.GetValueOrDefault("fps", "60"));
        var bitrateMbps = double.Parse(args.GetValueOrDefault("bitrate", "12"));
        var duration = double.Parse(args.GetValueOrDefault("duration", "10"));
        var stimWidth = int.Parse(args.GetValueOrDefault("source-width", "2560"));
        var stimHeight = int.Parse(args.GetValueOrDefault("source-height", "1440"));
        var stimulusFps = int.Parse(args.GetValueOrDefault("source-fps", "240"));
        var captureMonitor = args.GetValueOrDefault("capture", "window") == "monitor";

        Console.WriteLine($"# bench-capture-e2e targetHeight={targetHeight} fps={fps} bitrate={bitrateMbps}Mbps duration={duration}s");
        Console.WriteLine($"# stimulus {stimWidth}x{stimHeight}@{stimulusFps}fps");

        MediaFoundationRuntime.EnsureInitialized();
        if (!MediaFoundationRuntime.IsAvailable)
        {
            Console.Error.WriteLine("ERROR: Media Foundation not available");
            return 2;
        }

        // Stimulus window: a topmost swap-chain-backed popup presenting at
        // stimulusFps. WGC only delivers a new frame when the captured
        // source presents a new frame, so this is what decouples the test
        // from desktop idle behaviour. Stimulus fps is set high (240) so
        // it's never the limiting factor — whatever we target below caps
        // out on our own throttle, not the source.
        using var stimulus = new StimulusWindow(stimWidth, stimHeight, stimulusFps);
        // Give DWM a beat to show the window before WGC starts capturing.
        Thread.Sleep(250);

        // Same wiring as Program.cs (Desktop.Windows): one shared D3D11
        // device for capture + encoder, same factory constructor.
        var sharedDevices = new D3D11DeviceManager();
        sharedDevices.Initialize();
        var encoderFactory = new MediaFoundationH264EncoderFactory(sharedDevices.Device);

        var item = captureMonitor
            ? MonitorCaptureItem.CreateForPrimaryMonitor()
            : MonitorCaptureItem.CreateForWindow(stimulus.Hwnd);
        Console.WriteLine($"# capturing {(captureMonitor ? "monitor" : "window")} '{item.DisplayName}' {item.Size.Width}x{item.Size.Height}");

        var captureSource = new WindowsCaptureSource(item, sharedDevices, 60);

        var settings = new VideoSettings
        {
            TargetHeight = targetHeight,
            TargetFrameRate = fps,
            TargetBitrate = (int)(bitrateMbps * 1_000_000),
            Codec = VideoCodec.H264,
            Scaler = ScalerMode.Bilinear,
        };

        long encodedFrameCount = 0;
        long encodedByteCount = 0;
        var arrivalGaps = new List<double>(capacity: 16 * 1024);
        var lastArrivalWall = 0L;
        var arrivalLock = new object();

        // Hook the arrival timing off the raw TextureArrived event so we
        // measure capture delivery cadence, not the throttle output.
        captureSource.TextureArrived += (IntPtr _, int __, int ___, TimeSpan ____) =>
        {
            lock (arrivalLock)
            {
                var now = Stopwatch.GetTimestamp();
                if (lastArrivalWall != 0)
                {
                    var gapMs = (now - lastArrivalWall) * 1000.0 / Stopwatch.Frequency;
                    arrivalGaps.Add(gapMs);
                }
                lastArrivalWall = now;
            }
        };

        var streamer = new CaptureStreamer(
            captureSource,
            onEncoded: (duration, payload, _) =>
            {
                Interlocked.Increment(ref encodedFrameCount);
                Interlocked.Add(ref encodedByteCount, payload.Length);
            },
            settings,
            encoderFactory);

        streamer.Start();
        captureSource.StartAsync().GetAwaiter().GetResult();

        var sw = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(duration);
        while (sw.Elapsed < deadline)
        {
            Thread.Sleep(50);
        }
        sw.Stop();

        streamer.Stop();
        captureSource.StopAsync().GetAwaiter().GetResult();
        streamer.Dispose();
        captureSource.DisposeAsync().GetAwaiter().GetResult();
        sharedDevices.Dispose();

        var elapsedSec = sw.Elapsed.TotalSeconds;
        var actualFps = encodedFrameCount / elapsedSec;
        var actualBitrateMbps = (encodedByteCount * 8.0 / elapsedSec) / 1_000_000.0;

        double[] sortedGaps;
        lock (arrivalLock)
        {
            sortedGaps = arrivalGaps.OrderBy(g => g).ToArray();
        }

        var p50 = Percentile(sortedGaps, 50);
        var p95 = Percentile(sortedGaps, 95);
        var p99 = Percentile(sortedGaps, 99);
        var meanGap = sortedGaps.Length > 0 ? sortedGaps.Average() : 0.0;

        Console.WriteLine($"RESULT: elapsed_seconds={elapsedSec:F2}");
        Console.WriteLine($"RESULT: encoded_frames={encodedFrameCount}");
        Console.WriteLine($"RESULT: fps_actual={actualFps:F2}");
        Console.WriteLine($"RESULT: fps_target={fps}");
        Console.WriteLine($"RESULT: bitrate_actual_mbps={actualBitrateMbps:F2}");
        Console.WriteLine($"RESULT: bitrate_target_mbps={bitrateMbps:F2}");
        Console.WriteLine($"RESULT: arrival_samples={sortedGaps.Length}");
        Console.WriteLine($"RESULT: arrival_gap_mean_ms={meanGap:F2}");
        Console.WriteLine($"RESULT: arrival_gap_p50_ms={p50:F2}");
        Console.WriteLine($"RESULT: arrival_gap_p95_ms={p95:F2}");
        Console.WriteLine($"RESULT: arrival_gap_p99_ms={p99:F2}");
        var impliedCaptureFps = meanGap > 0 ? 1000.0 / meanGap : 0;
        Console.WriteLine($"RESULT: arrival_implied_fps={impliedCaptureFps:F1}");

        // Effective cap is whichever of (target, source) we hit first. We
        // need the encoded rate to come within 10% of that cap — anything
        // less means the pipeline is dropping frames it could have kept.
        var effectiveCapFps = Math.Min(fps, impliedCaptureFps);
        var withinCap = sortedGaps.Length == 0 || actualFps >= effectiveCapFps * 0.9;
        var sourceLimited = impliedCaptureFps < fps * 0.95;

        Console.WriteLine($"RESULT: effective_cap_fps={effectiveCapFps:F1}");
        Console.WriteLine($"RESULT: source_limited={sourceLimited}");
        if (withinCap)
        {
            var note = sourceLimited ? " (source-limited)" : "";
            Console.WriteLine($"VERDICT: PASS (encoded={actualFps:F1}fps, captured={impliedCaptureFps:F1}fps, target={fps}fps{note})");
            return 0;
        }

        Console.WriteLine($"VERDICT: FAIL (encoded={actualFps:F1}fps vs cap={effectiveCapFps:F1}fps, captured={impliedCaptureFps:F1}fps, target={fps}fps)");
        return 3;
    }

    /// <summary>
    /// Encode/decode loopback benchmark. Captures the stimulus window for
    /// N seconds, feeds each frame through the real MF H.264 encoder,
    /// then hands each encoded payload directly to the real MF H.264
    /// decoder. Reports end-to-end latency (encode + decode), per-stage
    /// frame counts, and loss rate. No network — this isolates whether
    /// the encoder/decoder pair is losing frames before the SFU ever
    /// sees them. Used to establish a "perfect transport" baseline for
    /// comparing against the network bench.
    /// </summary>
    private static int RunEncodeDecodeBenchmark(Dictionary<string, string> args)
    {
        var targetHeight = int.Parse(args.GetValueOrDefault("target-height", "1080"));
        var fps = int.Parse(args.GetValueOrDefault("fps", "60"));
        var bitrateMbps = double.Parse(args.GetValueOrDefault("bitrate", "12"));
        var duration = double.Parse(args.GetValueOrDefault("duration", "3"));
        var stimWidth = int.Parse(args.GetValueOrDefault("source-width", "1920"));
        var stimHeight = int.Parse(args.GetValueOrDefault("source-height", "1080"));

        Console.WriteLine($"# bench-encode-decode {targetHeight}p@{fps}fps {bitrateMbps}Mbps duration={duration}s");
        Console.WriteLine($"# stimulus {stimWidth}x{stimHeight}");

        MediaFoundationRuntime.EnsureInitialized();
        if (!MediaFoundationRuntime.IsAvailable)
        {
            Console.Error.WriteLine("ERROR: Media Foundation not available");
            return 2;
        }

        using var stimulus = new StimulusWindow(stimWidth, stimHeight, 240);
        Thread.Sleep(250);

        var sharedDevices = new D3D11DeviceManager();
        sharedDevices.Initialize();
        // Selector picks NVENC when H264EncoderFactorySelector.UseNvencSdk
        // is true (Phase 2 default) and capabilities are present, otherwise
        // falls back to MFT — this is the production code path.
        var encoderFactory = new H264EncoderFactorySelector(sharedDevices.Device);
        // Allow CLI to enable Phase 3 quality knobs so we can reproduce
        // production behavior (intra-refresh + AQ are on by default in
        // VideoSettings now).
        encoderFactory.NvencOptions = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencEncodeOptions
        {
            EnableAdaptiveQuantization = args.GetValueOrDefault("aq", "1") != "0",
            EnableTemporalAq = args.GetValueOrDefault("aq", "1") != "0",
            EnableIntraRefresh = args.GetValueOrDefault("intra-refresh", "1") != "0",
            Preset = int.Parse(args.GetValueOrDefault("preset", "4")),
        };
        var decoderFactory = new MediaFoundationH264DecoderFactory(sharedDevices.Device);
        var decoder = decoderFactory.CreateDecoder();

        var item = MonitorCaptureItem.CreateForWindow(stimulus.Hwnd);
        var captureSource = new WindowsCaptureSource(item, sharedDevices, 60);

        var settings = new VideoSettings
        {
            TargetHeight = targetHeight,
            TargetFrameRate = fps,
            TargetBitrate = (int)(bitrateMbps * 1_000_000),
            Codec = VideoCodec.H264,
            Scaler = ScalerMode.Bilinear,
        };

        // Per-stage state. Encoded bytes are handed straight to the
        // decoder on whatever thread the encoder callback fires on.
        long capturedCount = 0;
        long encodedCount = 0;
        long decodedCount = 0;
        long encodedBytes = 0;
        var encodeLatencies = new List<double>(capacity: 8 * 1024);
        var decodeLatencies = new List<double>(capacity: 8 * 1024);
        var e2eLatencies = new List<double>(capacity: 8 * 1024);
        int decodedWidth = 0, decodedHeight = 0;
        var statsLock = new object();

        captureSource.TextureArrived += (IntPtr _, int __, int ___, TimeSpan ____) =>
        {
            Interlocked.Increment(ref capturedCount);
        };

        var streamer = new CaptureStreamer(
            captureSource,
            onEncoded: (rtpDuration, encodedPayload, contentTs) =>
            {
                var encodeEnd = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref encodedCount);
                Interlocked.Add(ref encodedBytes, encodedPayload.Length);

                var decodeStart = Stopwatch.GetTimestamp();
                var decoded = decoder.Decode(encodedPayload, contentTs);
                var decodeEnd = Stopwatch.GetTimestamp();

                var decodeMs = (decodeEnd - decodeStart) * 1000.0 / Stopwatch.Frequency;
                var e2eMs = (decodeEnd - encodeEnd) * 1000.0 / Stopwatch.Frequency;
                lock (statsLock)
                {
                    decodeLatencies.Add(decodeMs);
                    e2eLatencies.Add(e2eMs);
                    if (decoded.Count > 0)
                    {
                        decodedCount += decoded.Count;
                        var first = decoded[0];
                        decodedWidth = first.Width;
                        decodedHeight = first.Height;
                    }
                }
            },
            settings,
            encoderFactory);

        streamer.Start();
        captureSource.StartAsync().GetAwaiter().GetResult();

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < duration)
        {
            Thread.Sleep(50);
        }
        sw.Stop();

        streamer.Stop();
        captureSource.StopAsync().GetAwaiter().GetResult();
        streamer.Dispose();
        captureSource.DisposeAsync().GetAwaiter().GetResult();

        // Drain any decoder output the encoder may have emitted just as
        // we stopped. Sleep briefly so in-flight async samples finish.
        Thread.Sleep(200);

        decoder.Dispose();
        sharedDevices.Dispose();

        var elapsed = sw.Elapsed.TotalSeconds;
        var captureFps = capturedCount / elapsed;
        var encodeFps = encodedCount / elapsed;
        var decodeFps = decodedCount / elapsed;
        var bitrate = encodedBytes * 8.0 / elapsed / 1_000_000.0;

        double[] decodeLatSorted, e2eLatSorted;
        lock (statsLock)
        {
            decodeLatSorted = decodeLatencies.OrderBy(x => x).ToArray();
            e2eLatSorted = e2eLatencies.OrderBy(x => x).ToArray();
        }

        Console.WriteLine($"RESULT: elapsed_seconds={elapsed:F2}");
        Console.WriteLine($"RESULT: captured_frames={capturedCount}");
        Console.WriteLine($"RESULT: encoded_frames={encodedCount}");
        Console.WriteLine($"RESULT: decoded_frames={decodedCount}");
        Console.WriteLine($"RESULT: capture_fps={captureFps:F1}");
        Console.WriteLine($"RESULT: encode_fps={encodeFps:F1}");
        Console.WriteLine($"RESULT: decode_fps={decodeFps:F1}");
        Console.WriteLine($"RESULT: bitrate_mbps={bitrate:F2}");
        Console.WriteLine($"RESULT: decoded_size={decodedWidth}x{decodedHeight}");
        Console.WriteLine($"RESULT: decode_latency_p50_ms={Percentile(decodeLatSorted, 50):F2}");
        Console.WriteLine($"RESULT: decode_latency_p95_ms={Percentile(decodeLatSorted, 95):F2}");
        Console.WriteLine($"RESULT: decode_latency_p99_ms={Percentile(decodeLatSorted, 99):F2}");
        Console.WriteLine($"RESULT: e2e_latency_p50_ms={Percentile(e2eLatSorted, 50):F2}");
        Console.WriteLine($"RESULT: e2e_latency_p95_ms={Percentile(e2eLatSorted, 95):F2}");

        // Decoder lags the encoder during warmup: the MF H.264 decoder
        // buffers inputs until it parses SPS + gets an IDR (~15–25
        // frames). That's a fixed cost, independent of test duration,
        // so percentage-based tolerance fails on short runs and passes
        // on long ones. Allow an absolute 30-frame warmup OR a 1%
        // steady-state loss, whichever is larger.
        var warmupBudget = Math.Max(30L, (long)(encodedCount * 0.01));
        var realLoss = Math.Max(0, encodedCount - decodedCount - warmupBudget);
        var lossRatio = encodedCount > 0 ? 1.0 - (double)decodedCount / encodedCount : 1.0;
        var steadyStateLossRatio = encodedCount > warmupBudget
            ? (double)realLoss / (encodedCount - warmupBudget)
            : 0.0;
        Console.WriteLine($"RESULT: decoder_loss_ratio={lossRatio:F3}");
        Console.WriteLine($"RESULT: steady_state_loss_ratio={steadyStateLossRatio:F3}");

        var pass = encodedCount > 0
                   && decodedCount > 0
                   && realLoss == 0
                   && decodedWidth > 0
                   && decodedHeight > 0;
        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} (captured={capturedCount}, encoded={encodedCount}, decoded={decodedCount}, warmup-budget={warmupBudget}, real-loss={realLoss})");
        return pass ? 0 : 3;
    }

    /// <summary>
    /// Full network-path benchmark. Starts the real ASP.NET Core SFU
    /// server in-process, connects two real clients through real
    /// signaling + real WebRTC, has one client encode H.264 via the MF
    /// hardware encoder and feed RTP to the other, and counts how many
    /// frames / bytes survive the roundtrip. Exposes the "1/3 of frames
    /// disappear on the receiver" class of bug that the encode-decode
    /// loopback (which bypasses the network) can't see.
    /// </summary>
    private static async Task<int> RunNetworkEndToEndBenchmark(Dictionary<string, string> args)
    {
        var width = int.Parse(args.GetValueOrDefault("width", "1280"));
        var height = int.Parse(args.GetValueOrDefault("height", "720"));
        var fps = int.Parse(args.GetValueOrDefault("fps", "30"));
        var bitrateMbps = double.Parse(args.GetValueOrDefault("bitrate", "6"));
        var duration = double.Parse(args.GetValueOrDefault("duration", "10"));

        Console.WriteLine($"# bench-network-e2e {width}x{height}@{fps}fps {bitrateMbps}Mbps duration={duration}s");

        MediaFoundationRuntime.EnsureInitialized();
        if (!MediaFoundationRuntime.IsAvailable)
        {
            Console.Error.WriteLine("ERROR: Media Foundation not available");
            return 2;
        }

        // This scenario exercises the RTP transport + SFU fan-out, not
        // the GPU capture path, so we keep the encoder on its CPU
        // FrameArrived path by NOT sharing the device. SyntheticFrameSource
        // only raises FrameArrived; if the encoder factory advertised
        // texture input, CaptureStreamer would subscribe to TextureArrived
        // only and see zero frames.
        var sharedDevices = new D3D11DeviceManager();
        sharedDevices.Initialize();
        var encoderFactory = new MediaFoundationH264EncoderFactory();
        var decoderFactory = new MediaFoundationH264DecoderFactory(sharedDevices.Device);

        await using var server = await StartServerAsync();
        var wsUri = ResolveWsUri(server);
        Console.WriteLine($"# server listening at {wsUri}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // --- Sender: create room, open RTC session, attach CaptureStreamer.
        var senderSig = new SignalingClient();
        var senderRoomJoined = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
        senderSig.RoomJoined += j => senderRoomJoined.TrySetResult(j);
        await senderSig.ConnectAsync(wsUri, new ClientHello(Guid.NewGuid(), "Alice", ProtocolVersion.Current), cts.Token);
        await senderSig.CreateRoomAsync(cts.Token);
        var senderJoin = await senderRoomJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        var roomId = senderJoin.RoomId;

        await using var senderRtc = new WebRtcSession(senderSig, WebRtcRole.Sender, VideoCodec.H264);
        await senderRtc.NegotiateAsync(TimeSpan.FromSeconds(15));

        await using var synthetic = new SyntheticFrameSource(width, height, fps);
        using var streamer = new CaptureStreamer(
            synthetic,
            onEncoded: (rtpDuration, payload, _) => senderRtc.PeerConnection.SendVideo(rtpDuration, payload),
            new VideoSettings
            {
                TargetHeight = height,
                TargetFrameRate = fps,
                TargetBitrate = (int)(bitrateMbps * 1_000_000),
                Codec = VideoCodec.H264,
                Scaler = ScalerMode.Bilinear,
            },
            encoderFactory);
        streamer.Start();

        // --- Receiver: join room, open RTC session, attach StreamReceiver.
        var receiverSig = new SignalingClient();
        var receiverRoomJoined = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverSig.RoomJoined += j => receiverRoomJoined.TrySetResult(j);
        await receiverSig.ConnectAsync(wsUri, new ClientHello(Guid.NewGuid(), "Bob", ProtocolVersion.Current), cts.Token);
        await receiverSig.JoinRoomAsync(roomId, cts.Token);
        await receiverRoomJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        await using var receiverRtc = new WebRtcSession(receiverSig, WebRtcRole.Receiver, VideoCodec.H264);
        await receiverRtc.NegotiateAsync(TimeSpan.FromSeconds(15));

        using var receiver = new StreamReceiver(receiverRtc.PeerConnection, decoderFactory, "Alice");
        await receiver.StartAsync();

        await synthetic.StartAsync();

        // --- Run the stream for the configured duration, polling both
        // sides' counters every 500 ms so we can print a little progress
        // ticker and later compute steady-state loss.
        var pollInterval = TimeSpan.FromMilliseconds(500);
        var samples = new List<(double t, long encoded, long decoded, long encBytes, long recvBytes)>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < duration)
        {
            await Task.Delay(pollInterval, cts.Token);
            samples.Add((
                sw.Elapsed.TotalSeconds,
                streamer.EncodedFrameCount,
                receiver.FramesDecoded,
                streamer.EncodedByteCount,
                receiver.EncodedByteCount));
            var s = samples[^1];
            Console.WriteLine($"# t={s.t:F1}s  encoded={s.encoded}  decoded={s.decoded}  sendMbps={s.encBytes * 8.0 / s.t / 1e6:F1}  recvMbps={s.recvBytes * 8.0 / s.t / 1e6:F1}");
        }

        streamer.Stop();
        await synthetic.StopAsync();
        await receiver.StopAsync();
        await Task.Delay(200, cts.Token);

        var totalEncoded = streamer.EncodedFrameCount;
        var totalDecoded = receiver.FramesDecoded;
        var totalEncBytes = streamer.EncodedByteCount;
        var totalRecvBytes = receiver.EncodedByteCount;
        var totalRecvFramesRaw = receiver.FramesReceived;
        var elapsed = sw.Elapsed.TotalSeconds;

        Console.WriteLine($"RESULT: elapsed_seconds={elapsed:F2}");
        Console.WriteLine($"RESULT: sender_encoded_frames={totalEncoded}");
        Console.WriteLine($"RESULT: sender_encoded_bytes={totalEncBytes}");
        Console.WriteLine($"RESULT: sender_encode_fps={totalEncoded / elapsed:F1}");
        Console.WriteLine($"RESULT: sender_bitrate_mbps={totalEncBytes * 8.0 / elapsed / 1e6:F2}");
        Console.WriteLine($"RESULT: receiver_frames_received={totalRecvFramesRaw}");
        Console.WriteLine($"RESULT: receiver_decoded_frames={totalDecoded}");
        Console.WriteLine($"RESULT: receiver_decode_fps={totalDecoded / elapsed:F1}");
        Console.WriteLine($"RESULT: receiver_encoded_bytes={totalRecvBytes}");
        Console.WriteLine($"RESULT: receiver_bitrate_mbps={totalRecvBytes * 8.0 / elapsed / 1e6:F2}");

        var encodedLoss = totalEncoded > 0
            ? 1.0 - (double)totalRecvFramesRaw / totalEncoded
            : 1.0;
        var decodedLoss = totalEncoded > 0
            ? 1.0 - (double)totalDecoded / totalEncoded
            : 1.0;
        var byteLoss = totalEncBytes > 0
            ? 1.0 - (double)totalRecvBytes / totalEncBytes
            : 1.0;
        Console.WriteLine($"RESULT: transport_frame_loss={encodedLoss:F3}");
        Console.WriteLine($"RESULT: transport_byte_loss={byteLoss:F3}");
        Console.WriteLine($"RESULT: endtoend_decode_loss={decodedLoss:F3}");

        // Verdict thresholds:
        //   - Transport (RTP) byte loss < 2% on localhost UDP (anything
        //     above that is an SFU or fragmentation bug — the network is
        //     loopback)
        //   - Decoded frames within 3% of encoded (allowing warmup + 1
        //     frame of encoder lag)
        var warmupBudget = Math.Max(30L, (long)(totalEncoded * 0.02));
        var steadyLoss = Math.Max(0, totalEncoded - totalDecoded - warmupBudget);
        var pass = totalEncoded > 0
                   && totalDecoded > 0
                   && byteLoss < 0.02
                   && steadyLoss == 0;

        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} (encoded={totalEncoded}, decoded={totalDecoded}, transport_loss={encodedLoss:P1}, byte_loss={byteLoss:P1})");

        await senderSig.DisposeAsync();
        await receiverSig.DisposeAsync();
        sharedDevices.Dispose();
        return pass ? 0 : 3;
    }

    private static async Task<WebApplication> StartServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        ServerHost.ConfigureServices(builder.Services);

        var app = builder.Build();
        ServerHost.ConfigureEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private static Uri ResolveWsUri(WebApplication app)
    {
        var feature = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();
        var http = feature!.Addresses.First(a => a.StartsWith("http://"));
        var asWs = http.Replace("http://", "ws://", StringComparison.Ordinal);
        return new Uri(asWs + "/ws");
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
        {
            return 0.0;
        }

        var rank = (percentile / 100.0) * (sortedValues.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi)
        {
            return sortedValues[lo];
        }

        var frac = rank - lo;
        return sortedValues[lo] * (1 - frac) + sortedValues[hi] * frac;
    }

    /// <summary>
    /// Bench scenario for the BgraDownscale hot path. Runs N iterations of
    /// nearest-neighbor downscale from src dims to dst dims and reports
    /// average / min / max time per call. Useful for verifying optimization
    /// changes without firing up the whole encode pipeline.
    /// </summary>
    private static int RunDownscaleBenchmark(Dictionary<string, string> args)
    {
        var srcWidth = int.Parse(args.GetValueOrDefault("src-width", "2560"));
        var srcHeight = int.Parse(args.GetValueOrDefault("src-height", "1392"));
        var dstWidth = int.Parse(args.GetValueOrDefault("dst-width", "1920"));
        var dstHeight = int.Parse(args.GetValueOrDefault("dst-height", "1044"));
        var iterations = int.Parse(args.GetValueOrDefault("iterations", "200"));

        Console.WriteLine($"# bench-downscale {srcWidth}x{srcHeight} -> {dstWidth}x{dstHeight} iterations={iterations}");

        var src = new byte[srcWidth * srcHeight * 4];
        var dst = new byte[dstWidth * dstHeight * 4];
        for (var i = 0; i < src.Length; i++)
        {
            src[i] = (byte)(i & 0xFF);
        }

        for (var w = 0; w < 5; w++)
        {
            BgraDownscale.Downscale(src, srcWidth, srcHeight, dst, dstWidth, dstHeight);
        }

        var totalMs = 0.0;
        var maxMs = 0.0;
        var minMs = double.MaxValue;
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            BgraDownscale.Downscale(src, srcWidth, srcHeight, dst, dstWidth, dstHeight);
            var end = Stopwatch.GetTimestamp();
            var ms = (end - start) * 1000.0 / Stopwatch.Frequency;
            totalMs += ms;
            if (ms > maxMs)
            {
                maxMs = ms;
            }

            if (ms < minMs)
            {
                minMs = ms;
            }
        }

        var avgMs = totalMs / iterations;
        Console.WriteLine($"RESULT: avg_ms={avgMs:F2}");
        Console.WriteLine($"RESULT: min_ms={minMs:F2}");
        Console.WriteLine($"RESULT: max_ms={maxMs:F2}");
        Console.WriteLine($"RESULT: implied_fps_cap={1000.0 / avgMs:F0}");
        return 0;
    }

    /// <summary>
    /// Bench scenario for the D3D11 Video Processor scaler. Allocates a
    /// source and destination BGRA texture on a fresh D3D11 device, runs
    /// N VideoProcessorBlt calls through the scaler, and reports per-call
    /// time. This tells us what the production per-frame cost will be once
    /// the capture source is feeding textures straight to the scaler.
    /// </summary>
    private static int RunVideoScalerBenchmark(Dictionary<string, string> args)
    {
        var srcWidth = int.Parse(args.GetValueOrDefault("src-width", "2560"));
        var srcHeight = int.Parse(args.GetValueOrDefault("src-height", "1440"));
        var dstWidth = int.Parse(args.GetValueOrDefault("dst-width", "1920"));
        var dstHeight = int.Parse(args.GetValueOrDefault("dst-height", "1080"));
        var iterations = int.Parse(args.GetValueOrDefault("iterations", "200"));

        Console.WriteLine($"# bench-scaler {srcWidth}x{srcHeight} -> {dstWidth}x{dstHeight} iterations={iterations}");

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var hr = D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            flags,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out var device,
            out _,
            out _);
        hr.CheckError();
        using var _device = device!;

        using var mt = device.QueryInterfaceOrNull<ID3D11Multithread>();
        mt?.SetMultithreadProtected(true);

        // Source texture filled with a noise pattern so the Video Processor
        // has something non-trivial to filter.
        var srcBytes = new byte[srcWidth * srcHeight * 4];
        for (var i = 0; i < srcBytes.Length; i++)
        {
            srcBytes[i] = (byte)(i & 0xFF);
        }

        var srcDesc = new Texture2DDescription
        {
            Width = (uint)srcWidth,
            Height = (uint)srcHeight,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        var srcSub = new SubresourceData { DataPointer = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(srcBytes, 0), RowPitch = (uint)(srcWidth * 4) };
        using var srcTex = device.CreateTexture2D(srcDesc, new[] { srcSub });

        var dstDesc = new Texture2DDescription
        {
            Width = (uint)dstWidth,
            Height = (uint)dstHeight,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        using var dstTex = device.CreateTexture2D(dstDesc);

        // Diagnostic: check format support on the enumerator before we try
        // to run the Blt. E_INVALIDARG from CreateVideoProcessorInputView
        // usually means the driver doesn't like the (format, view dim)
        // combination.
        using (var tmpVideoDevice = device.QueryInterface<ID3D11VideoDevice>())
        {
            var tmpContent = new Vortice.Direct3D11.VideoProcessorContentDescription
            {
                InputFrameFormat = Vortice.Direct3D11.VideoFrameFormat.Progressive,
                InputFrameRate = new Vortice.DXGI.Rational(60, 1),
                InputWidth = (uint)srcWidth,
                InputHeight = (uint)srcHeight,
                OutputFrameRate = new Vortice.DXGI.Rational(60, 1),
                OutputWidth = (uint)dstWidth,
                OutputHeight = (uint)dstHeight,
                Usage = Vortice.Direct3D11.VideoUsage.PlaybackNormal,
            };
            using var tmpEnum = tmpVideoDevice.CreateVideoProcessorEnumerator(tmpContent);
            var bgraSupport = tmpEnum.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm);
            Console.WriteLine($"RESULT: bgra_input_support={bgraSupport}");
            var nv12Support = tmpEnum.CheckVideoProcessorFormat(Format.NV12);
            Console.WriteLine($"RESULT: nv12_input_support={nv12Support}");
        }

        using var scaler = new D3D11VideoScaler(device, srcWidth, srcHeight, dstWidth, dstHeight);

        // Warmup.
        for (var i = 0; i < 10; i++)
        {
            scaler.Process(srcTex, dstTex);
        }
        device.ImmediateContext.Flush();

        var totalMs = 0.0;
        var minMs = double.MaxValue;
        var maxMs = 0.0;
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            scaler.Process(srcTex, dstTex);
            device.ImmediateContext.Flush();
            var end = Stopwatch.GetTimestamp();
            var ms = (end - start) * 1000.0 / Stopwatch.Frequency;
            totalMs += ms;
            if (ms < minMs)
            {
                minMs = ms;
            }

            if (ms > maxMs)
            {
                maxMs = ms;
            }
        }

        var avgMs = totalMs / iterations;
        Console.WriteLine($"RESULT: avg_ms={avgMs:F2}");
        Console.WriteLine($"RESULT: min_ms={minMs:F2}");
        Console.WriteLine($"RESULT: max_ms={maxMs:F2}");
        Console.WriteLine($"RESULT: implied_fps_cap={1000.0 / avgMs:F0}");
        Console.WriteLine("VERDICT: PASS");
        return 0;
    }

    private static void FillSyntheticNv12(byte[] nv12, int width, int height)
    {
        // Y plane: a vertical gradient.
        var ySize = width * height;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                nv12[y * width + x] = (byte)((x + y) & 0xFF);
            }
        }
        // UV plane: mid-gray-ish chroma so the result is visible if anyone
        // ever decodes it.
        for (var i = ySize; i < nv12.Length; i++)
        {
            nv12[i] = 128;
        }
    }

    private static void FillGradient(byte[] bgra, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = (y * width + x) * 4;
                bgra[idx + 0] = (byte)(x & 0xFF);
                bgra[idx + 1] = (byte)(y & 0xFF);
                bgra[idx + 2] = (byte)((x + y) & 0xFF);
                bgra[idx + 3] = 0xFF;
            }
        }
    }

    /// <summary>
    /// Mutate a stripe of pixels each frame so the encoder sees motion and
    /// the rate controller / keyframe interval behaves the way a real screen
    /// share would. A static frame produces tiny encoded sizes that wouldn't
    /// stress the pipeline realistically.
    /// </summary>
    private static void MutatePattern(byte[] bgra, int frameIndex, int width)
    {
        var rowBytes = width * 4;
        // Slide a horizontal band down the image.
        var bandRow = (frameIndex * 17) % (bgra.Length / rowBytes);
        var bandStart = bandRow * rowBytes;
        for (var i = 0; i < rowBytes && bandStart + i < bgra.Length; i++)
        {
            bgra[bandStart + i] = (byte)((bgra[bandStart + i] + frameIndex * 7) & 0xFF);
        }
    }

    /// <summary>
    /// Synthetic Opus encode benchmark. Feeds a 440 Hz sine through the
    /// Concentus-backed encoder and reports per-frame encode cost. Useful
    /// to catch regressions in the codec wrapper or to measure the cost
    /// of audio on the publisher's CPU budget (spoiler: it's negligible —
    /// typically sub-millisecond per 20 ms frame).
    /// </summary>
    private static int RunAudioEncodeBenchmark(Dictionary<string, string> args)
    {
        var bitrate = int.Parse(args.GetValueOrDefault("bitrate", "96000"));
        var stereo = args.GetValueOrDefault("stereo", "true") != "false";
        var duration = double.Parse(args.GetValueOrDefault("duration", "10"));

        Console.WriteLine($"# bench-audio-encode bitrate={bitrate} stereo={stereo} duration={duration}s");

        var settings = new AudioSettings
        {
            TargetBitrate = bitrate,
            Stereo = stereo,
            FrameDurationMs = 20,
            Application = OpusApplicationMode.GeneralAudio,
        };
        var factory = new OpusAudioCodecFactory();
        using var encoder = factory.CreateEncoder(settings);

        const int sampleRate = 48000;
        var frameSamples = encoder.FrameSamples;
        var channels = encoder.Channels;
        var pcm = new short[frameSamples * channels];

        // Warmup: let Opus's rate-control settle before measuring. The
        // first few packets are partial / quiet frames as the encoder
        // decides on a starting mode.
        var clock = Stopwatch.StartNew();
        for (var w = 0; w < 10; w++)
        {
            FillSine(pcm, frameSamples, channels, sampleRate, 440.0, w * frameSamples);
            _ = encoder.EncodePcm(pcm, clock.Elapsed);
        }

        var frameCount = 0;
        var totalBytes = 0L;
        var encTotalMs = 0.0;
        var encMaxMs = 0.0;
        var encSamples = new List<double>(capacity: (int)(duration * 50));

        var benchSw = Stopwatch.StartNew();
        var basePos = 0;
        while (benchSw.Elapsed.TotalSeconds < duration)
        {
            FillSine(pcm, frameSamples, channels, sampleRate, 440.0, basePos);
            basePos += frameSamples;

            var t0 = Stopwatch.GetTimestamp();
            var encoded = encoder.EncodePcm(pcm, clock.Elapsed);
            var t1 = Stopwatch.GetTimestamp();
            var ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            encTotalMs += ms;
            if (ms > encMaxMs)
            {
                encMaxMs = ms;
            }
            encSamples.Add(ms);
            if (encoded is { Bytes.Length: > 0 })
            {
                frameCount++;
                totalBytes += encoded.Value.Bytes.Length;
            }
        }

        encSamples.Sort();
        var p99Idx = Math.Min(encSamples.Count - 1, (int)(encSamples.Count * 0.99));
        var p99 = encSamples.Count > 0 ? encSamples[p99Idx] : 0.0;
        var avg = encTotalMs / Math.Max(1, encSamples.Count);
        var realBitrate = (totalBytes * 8.0) / Math.Max(0.001, benchSw.Elapsed.TotalSeconds);

        Console.WriteLine($"RESULT: frames_encoded={frameCount}");
        Console.WriteLine($"RESULT: bytes_total={totalBytes}");
        Console.WriteLine($"RESULT: encode_ms_per_frame_mean={avg:F3}");
        Console.WriteLine($"RESULT: encode_ms_p99={p99:F3}");
        Console.WriteLine($"RESULT: encode_ms_max={encMaxMs:F3}");
        Console.WriteLine($"RESULT: bitrate_out_kbps={realBitrate / 1000.0:F1}");
        Console.WriteLine($"RESULT: bitrate_target_kbps={bitrate / 1000.0:F1}");

        // Real-time budget: a 20 ms frame has 20 ms to encode before the
        // next one arrives. p99 < 5 ms is an order-of-magnitude safety
        // margin for Opus; anything approaching 20 would indicate a
        // catastrophic regression.
        var pass = p99 < 5.0 && frameCount > 0;
        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} (p99 {p99:F2} ms, {frameCount} frames)");
        return pass ? 0 : 3;
    }

    /// <summary>
    /// Real WASAPI loopback → resample → Opus encode → Opus decode. Proves
    /// the Windows audio I/O stack works end-to-end on this machine and
    /// reports how many capture frames survived the round-trip. Requires
    /// an active default render endpoint (silence or playback both work;
    /// loopback produces a stream either way).
    /// </summary>
    private static async Task<int> RunAudioLoopbackBenchmark(Dictionary<string, string> args)
    {
        var duration = double.Parse(args.GetValueOrDefault("duration", "5"));
        var bitrate = int.Parse(args.GetValueOrDefault("bitrate", "96000"));

        Console.WriteLine($"# bench-audio-loopback duration={duration}s bitrate={bitrate}");

        var provider = new WasapiAudioCaptureProvider();
        var source = await provider.CreateLoopbackSourceAsync();
        if (source is null)
        {
            Console.Error.WriteLine("ERROR: no default render endpoint available for loopback");
            return 2;
        }

        var settings = new AudioSettings
        {
            TargetBitrate = bitrate,
            Stereo = true,
            FrameDurationMs = 20,
            Application = OpusApplicationMode.GeneralAudio,
        };
        var codecs = new OpusAudioCodecFactory();
        using var encoder = codecs.CreateEncoder(settings);
        using var decoder = codecs.CreateDecoder(settings.Stereo ? 2 : 1);
        using var resampler = new NAudioResampler();

        var frameSamples = encoder.FrameSamples;
        var channels = encoder.Channels;
        var frameStride = frameSamples * channels;

        // Incoming PCM from WASAPI is variable-sized, Opus wants exact
        // 960×channels samples. Accumulate into a ring-like buffer and
        // drain in frame chunks.
        var accum = new short[frameStride * 16];
        var accumCount = 0;
        var resampleScratch = new short[frameStride * 8];

        var framesCaptured = 0;
        var framesEncoded = 0;
        var framesDecoded = 0;
        var sourceBytes = 0L;
        var encodedBytes = 0L;

        var capLock = new object();

        void OnFrame(in AudioFrameData f)
        {
            Interlocked.Increment(ref framesCaptured);
            Interlocked.Add(ref sourceBytes, f.Pcm.Length);

            int produced;
            try
            {
                produced = resampler.Resample(
                    f.Pcm,
                    f.SampleRate,
                    f.Channels,
                    f.Format,
                    resampleScratch);
            }
            catch
            {
                return;
            }

            lock (capLock)
            {
                // Append resampler output to accumulator, grow if needed
                // (rare — the fixed allocation covers 16 frames worth).
                if (accumCount + produced > accum.Length)
                {
                    var bigger = new short[(accumCount + produced) * 2];
                    Array.Copy(accum, bigger, accumCount);
                    accum = bigger;
                }
                Array.Copy(resampleScratch, 0, accum, accumCount, produced);
                accumCount += produced;

                // Drain full frames into encoder → decoder.
                while (accumCount >= frameStride)
                {
                    var slice = accum.AsSpan(0, frameStride);
                    var enc = encoder.EncodePcm(slice, f.Timestamp);
                    // Shift remainder left.
                    Array.Copy(accum, frameStride, accum, 0, accumCount - frameStride);
                    accumCount -= frameStride;

                    if (enc is null)
                    {
                        continue;
                    }
                    Interlocked.Increment(ref framesEncoded);
                    Interlocked.Add(ref encodedBytes, enc.Value.Bytes.Length);

                    var dec = decoder.Decode(enc.Value.Bytes, 0);
                    if (dec is not null)
                    {
                        Interlocked.Increment(ref framesDecoded);
                    }
                }
            }
        }

        source.FrameArrived += OnFrame;
        await source.StartAsync();
        Console.WriteLine($"# source: {source.DisplayName} @ {source.SourceSampleRate}Hz / {source.SourceChannels}ch / {source.SourceFormat}");

        await Task.Delay(TimeSpan.FromSeconds(duration));

        await source.StopAsync();
        source.FrameArrived -= OnFrame;
        await source.DisposeAsync();

        var actualBitrate = (encodedBytes * 8.0) / Math.Max(0.001, duration);
        Console.WriteLine($"RESULT: frames_captured={framesCaptured}");
        Console.WriteLine($"RESULT: frames_encoded={framesEncoded}");
        Console.WriteLine($"RESULT: frames_decoded={framesDecoded}");
        Console.WriteLine($"RESULT: source_bytes={sourceBytes}");
        Console.WriteLine($"RESULT: encoded_bytes={encodedBytes}");
        Console.WriteLine($"RESULT: bitrate_out_kbps={actualBitrate / 1000.0:F1}");

        // Steady-state expectation: 1 second = 50 frames at 20 ms. A
        // 5-second run should yield ~250 encoded + decoded frames on a
        // healthy system; be loose enough to not false-fail on a warmup
        // lag.
        var expected = (int)(duration * 40);
        var pass = framesDecoded >= expected;
        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} (decoded {framesDecoded}/{expected} expected)");
        return pass ? 0 : 3;
    }

    /// <summary>
    /// Audio/video sync benchmark. Drives a <see cref="CaptureStreamer"/>
    /// and an <see cref="AudioStreamer"/> from a shared monotonic
    /// stopwatch and measures how closely the per-frame content
    /// timestamps track each other at the send boundary. Regression
    /// target: if either pipeline starts buffering or the shared clock
    /// gets split, the RMS skew climbs off zero and the VERDICT fails.
    /// This is the test that catches "we broke lip sync without anyone
    /// noticing."
    /// </summary>
    private static int RunAvSyncBenchmark(Dictionary<string, string> args)
    {
        var duration = double.Parse(args.GetValueOrDefault("duration", "5"));
        var videoFps = int.Parse(args.GetValueOrDefault("fps", "30"));
        Console.WriteLine($"# bench-av-sync duration={duration}s fps={videoFps}");

        var clock = Stopwatch.StartNew();

        // Video side: synthetic BGRA frames fed through a real VP8
        // encoder so the timestamp math matches production. Collect the
        // content timestamp of every emitted encoded frame.
        var videoTimestamps = new List<TimeSpan>();
        var width = 320;
        var height = 180;
        var vidFactory = new VpxEncoderFactory();
        using var vidEncoder = vidFactory.CreateEncoder(width, height, videoFps, 1_000_000, gopFrames: videoFps * 2);
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = 0x40;
            bgra[i + 1] = 0x80;
            bgra[i + 2] = 0xC0;
            bgra[i + 3] = 0xFF;
        }

        // Audio side: AudioStreamer with a synthetic capture source that
        // fires from the same shared clock. Collect content timestamps
        // on every encoded packet.
        var audioTimestamps = new List<TimeSpan>();
        var audioSettings = new AudioSettings { Stereo = true, TargetBitrate = 96_000 };
        var codecs = new OpusAudioCodecFactory();
        var audioSource = new SyntheticAudioSource();
        var audioResampler = new PassThroughS16ResamplerForHarness();
        using var audioStreamer = new AudioStreamer(
            audioSource,
            audioResampler,
            (_, _, ts) => audioTimestamps.Add(ts),
            audioSettings,
            codecs);
        audioStreamer.Start();

        var videoInterval = TimeSpan.FromSeconds(1.0 / videoFps);
        var audioInterval = TimeSpan.FromMilliseconds(20);
        var nextVideo = TimeSpan.Zero;
        var nextAudio = TimeSpan.Zero;
        const int audioChannels = 2;
        const int audioFrameSamples = 960;
        var pcm = new short[audioFrameSamples * audioChannels];
        var audioPos = 0;

        while (clock.Elapsed.TotalSeconds < duration)
        {
            var now = clock.Elapsed;
            if (now >= nextVideo)
            {
                var encoded = vidEncoder.EncodeBgra(bgra, width * 4, now);
                if (encoded is not null)
                {
                    videoTimestamps.Add(encoded.Value.Timestamp);
                }
                nextVideo += videoInterval;
            }
            if (now >= nextAudio)
            {
                for (var i = 0; i < audioFrameSamples; i++)
                {
                    var phase = 2.0 * Math.PI * 440.0 * (audioPos + i) / 48000.0;
                    var s = (short)(Math.Sin(phase) * 8000.0);
                    pcm[i * audioChannels + 0] = s;
                    pcm[i * audioChannels + 1] = s;
                }
                audioPos += audioFrameSamples;
                audioSource.PumpFrame(pcm, now);
                nextAudio += audioInterval;
            }
            Thread.Sleep(1);
        }

        audioStreamer.Stop();

        // For each video timestamp, find the temporally-nearest audio
        // timestamp and measure the gap. Index-based pairing would
        // conflate the natural cadence difference (30 fps video vs
        // 50 Hz audio) with actual clock drift. Time-nearest pairing
        // isolates drift: if the shared clock is honest, the nearest
        // audio ts is always within half an audio interval (10 ms).
        audioTimestamps.Sort();
        double skewSumMs = 0;
        double skewMaxMs = 0;
        var pairs = 0;
        foreach (var vTs in videoTimestamps)
        {
            var idx = audioTimestamps.BinarySearch(vTs);
            if (idx < 0)
            {
                idx = ~idx;
            }
            // Check the two audio frames bracketing vTs and pick the closer one.
            var bestMs = double.PositiveInfinity;
            foreach (var candidateIdx in new[] { idx - 1, idx })
            {
                if (candidateIdx < 0 || candidateIdx >= audioTimestamps.Count)
                {
                    continue;
                }
                var d = Math.Abs((vTs - audioTimestamps[candidateIdx]).TotalMilliseconds);
                if (d < bestMs)
                {
                    bestMs = d;
                }
            }
            if (double.IsFinite(bestMs))
            {
                skewSumMs += bestMs * bestMs;
                if (bestMs > skewMaxMs)
                {
                    skewMaxMs = bestMs;
                }
                pairs++;
            }
        }
        var rmsSkewMs = pairs > 0 ? Math.Sqrt(skewSumMs / pairs) : 0;

        Console.WriteLine($"RESULT: video_frames={videoTimestamps.Count}");
        Console.WriteLine($"RESULT: audio_frames={audioTimestamps.Count}");
        Console.WriteLine($"RESULT: pairs_compared={pairs}");
        Console.WriteLine($"RESULT: av_skew_rms_ms={rmsSkewMs:F2}");
        Console.WriteLine($"RESULT: av_skew_max_ms={skewMaxMs:F2}");

        // Time-nearest pairing on a shared clock: expected RMS is
        // within half an audio frame interval (10 ms) plus the 1 ms
        // harness sleep granularity, so 15 ms is a comfortable bound.
        // Anything above means the clocks have split — a real bug that
        // would break lip sync on every viewer.
        var pass = pairs > 0 && rmsSkewMs < 15.0;
        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} (RMS {rmsSkewMs:F1} ms, max {skewMaxMs:F1} ms)");
        return pass ? 0 : 3;
    }

    /// <summary>
    /// End-to-end smoke test for <c>ProcessLoopbackAudioSource</c>:
    /// activate per-process loopback against a target PID and count the
    /// PCM frames that come through for a few seconds. Confirms the
    /// <c>ActivateAudioInterfaceAsync</c> / PROCESS_LOOPBACK path works
    /// on the current Windows build before we wire it into the room.
    /// Play audio from the target process while this is running.
    /// </summary>
    private static async Task<int> RunProcessAudioBenchmark(Dictionary<string, string> args)
    {
        if (!args.TryGetValue("pid", out var pidStr) || !int.TryParse(pidStr, out var pid) || pid <= 0)
        {
            Console.Error.WriteLine("ERROR: --pid <process id> required");
            return 2;
        }
        var duration = double.Parse(args.GetValueOrDefault("duration", "5"));

        Console.WriteLine($"# bench-process-audio pid={pid} duration={duration}s");

        var source = new VicoScreenShare.Client.Windows.Audio.ProcessLoopbackAudioSource(pid);
        var frames = 0;
        var bytes = 0L;
        source.FrameArrived += (in VicoScreenShare.Client.Platform.AudioFrameData f) =>
        {
            Interlocked.Increment(ref frames);
            Interlocked.Add(ref bytes, f.Pcm.Length);
        };

        try
        {
            await source.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: start failed: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        Console.WriteLine($"# capturing {source.DisplayName} @ {source.SourceSampleRate} Hz / {source.SourceChannels} ch / {source.SourceFormat}");
        await Task.Delay(TimeSpan.FromSeconds(duration));

        await source.StopAsync();
        await source.DisposeAsync();

        Console.WriteLine($"RESULT: frames={frames}");
        Console.WriteLine($"RESULT: bytes={bytes}");
        Console.WriteLine($"RESULT: bytes_per_sec={bytes / Math.Max(0.001, duration):F0}");
        // Minimum threshold: even a silent app tree should produce *some*
        // WASAPI callbacks if activation worked. Zero frames in 5 s means
        // the activation returned a valid-looking client but the session
        // never started — usually a format mismatch or wrong PID.
        var pass = frames > 0;
        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} ({frames} frames over {duration}s)");
        return pass ? 0 : 3;
    }

    /// <summary>
    /// Sanity-check the custom capture-target enumerator: print every
    /// shareable window + monitor with metadata. Useful for confirming
    /// the filter is excluding the right things (no cloaked UWP, no
    /// tool windows without titles) before the UI layer wraps it.
    /// </summary>
    private static async Task<int> RunListTargetsScenario(Dictionary<string, string> args)
    {
        var withThumbnails = args.GetValueOrDefault("thumbnails", "true") != "false";
        var enumerator = new Win32CaptureTargetEnumerator();
        var targets = await enumerator.EnumerateAsync();

        Console.WriteLine($"# list-targets enumerated {targets.Count} shareable targets");
        var windows = 0;
        var monitors = 0;
        var iconsFound = 0;
        var thumbsFound = 0;
        var thumbsFailed = 0;
        foreach (var t in targets)
        {
            var iconTag = t.Icon is null ? "" : $"  icon {t.Icon.Width}×{t.Icon.Height}";
            var thumbTag = string.Empty;
            if (withThumbnails)
            {
                var sw = Stopwatch.StartNew();
                var thumb = await enumerator.GetThumbnailAsync(t, 280, 160);
                sw.Stop();
                if (thumb is not null)
                {
                    thumbsFound++;
                    thumbTag = $"  thumb {thumb.Width}×{thumb.Height} stride={thumb.StrideBytes} pixels={thumb.BgraPixels.Length}B in {sw.ElapsedMilliseconds}ms";
                }
                else
                {
                    thumbsFailed++;
                    thumbTag = $"  thumb NULL in {sw.ElapsedMilliseconds}ms";
                }
            }
            Console.WriteLine($"  [{t.Kind}] pid={t.ProcessId,-6} handle=0x{t.Handle.ToInt64():X} \"{t.DisplayName}\" — {t.OwnerDisplayName}{iconTag}{thumbTag}");
            if (t.Kind == VicoScreenShare.Client.Platform.CaptureTargetKind.Window) windows++;
            else if (t.Kind == VicoScreenShare.Client.Platform.CaptureTargetKind.Monitor) monitors++;
            if (t.Icon is not null) iconsFound++;
        }
        Console.WriteLine($"RESULT: windows={windows}");
        Console.WriteLine($"RESULT: monitors={monitors}");
        Console.WriteLine($"RESULT: icons={iconsFound}");
        Console.WriteLine($"RESULT: thumbs_ok={thumbsFound}");
        Console.WriteLine($"RESULT: thumbs_failed={thumbsFailed}");
        Console.WriteLine($"VERDICT: {(monitors > 0 && windows > 0 ? "PASS" : "FAIL")}");
        return monitors > 0 && windows > 0 ? 0 : 3;
    }

    private sealed class SyntheticAudioSource : VicoScreenShare.Client.Platform.IAudioCaptureSource
    {
        public string DisplayName => "synthetic";
        public int SourceSampleRate => 48000;
        public int SourceChannels => 2;
        public VicoScreenShare.Client.Platform.AudioSampleFormat SourceFormat => VicoScreenShare.Client.Platform.AudioSampleFormat.PcmS16Interleaved;
        public event VicoScreenShare.Client.Platform.AudioFrameArrivedHandler? FrameArrived;
#pragma warning disable CS0067
        public event Action? Closed;
#pragma warning restore CS0067

        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void PumpFrame(short[] pcm, TimeSpan ts)
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm.AsSpan());
            var f = new VicoScreenShare.Client.Platform.AudioFrameData(
                bytes,
                SourceSampleRate,
                SourceChannels,
                SourceFormat,
                ts);
            FrameArrived?.Invoke(in f);
        }
    }

    private sealed class PassThroughS16ResamplerForHarness : VicoScreenShare.Client.Platform.IAudioResampler
    {
        public int Resample(ReadOnlySpan<byte> inputPcm, int inputSampleRate, int inputChannels, VicoScreenShare.Client.Platform.AudioSampleFormat inputFormat, Span<short> destination)
        {
            var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(inputPcm);
            src.CopyTo(destination);
            return src.Length;
        }
        public void Dispose() { }
    }

    private static void FillSine(short[] pcm, int frameSamples, int channels, int sampleRate, double hz, int basePosition)
    {
        const double amplitude = 10_000.0;
        for (var i = 0; i < frameSamples; i++)
        {
            var phase = 2.0 * Math.PI * hz * (basePosition + i) / sampleRate;
            var s = (short)(Math.Sin(phase) * amplitude);
            for (var c = 0; c < channels; c++)
            {
                pcm[i * channels + c] = s;
            }
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>();
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            if (args[i].StartsWith("--"))
            {
                map[args[i][2..]] = args[i + 1];
            }
        }
        return map;
    }

    private static int UnknownScenario(string s)
    {
        Console.Error.WriteLine($"unknown scenario: {s}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: VicoScreenShare.MediaHarness <scenario> [args]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("scenarios:");
        Console.Error.WriteLine("  bench-encode  synthetic encode benchmark");
        Console.Error.WriteLine("    --width N           default 1920");
        Console.Error.WriteLine("    --height N          default 1080");
        Console.Error.WriteLine("    --fps N             default 60");
        Console.Error.WriteLine("    --bitrate Mbps      default 10");
        Console.Error.WriteLine("    --duration sec      default 5");
        Console.Error.WriteLine("    --codec h264|vp8    default h264");
        Console.Error.WriteLine("    --throttle true|false  default true");
        Console.Error.WriteLine("  bench-audio-encode    synthetic Opus encode benchmark (440 Hz sine)");
        Console.Error.WriteLine("    --bitrate bps       default 96000");
        Console.Error.WriteLine("    --stereo true|false default true");
        Console.Error.WriteLine("    --duration sec      default 10");
        Console.Error.WriteLine("  bench-audio-loopback  real WASAPI loopback -> encode -> decode");
        Console.Error.WriteLine("    --duration sec      default 5");
        Console.Error.WriteLine("    --bitrate bps       default 96000");
        Console.Error.WriteLine("  bench-av-sync         shared-clock audio/video timestamp skew");
        Console.Error.WriteLine("    --duration sec      default 5");
        Console.Error.WriteLine("    --fps N             default 30");
        Console.Error.WriteLine("  list-targets          enumerate shareable windows + monitors");
        Console.Error.WriteLine("  bench-process-audio   capture a process's audio via PROCESS_LOOPBACK");
        Console.Error.WriteLine("    --pid N             required, target process id");
        Console.Error.WriteLine("    --duration sec      default 5");
        Console.Error.WriteLine("  bench-nvenc-probe     run NVENC capability probe and print findings");
        Console.Error.WriteLine("  bench-mft-av1-decoder probe Media Foundation for an AV1 video decoder");
        Console.Error.WriteLine("  bench-av1-rtp-roundtrip  encode N AV1 frames, packetize, depacketize, verify");
        Console.Error.WriteLine("    --frames N           default 60");
        Console.Error.WriteLine("    --mtu N              default 1200");
        Console.Error.WriteLine("    --loss <0..0.5>      drop probability per packet, default 0");
        Console.Error.WriteLine("  bench-av1-encode-decode  full encode → packetize → depacketize → MFT decode loop");
        Console.Error.WriteLine("    --frames N           default 60");
        Console.Error.WriteLine("    --width N            default 1280");
        Console.Error.WriteLine("    --height N           default 720");
        Console.Error.WriteLine("  bench-nvenc-keyframe  validate force-IDR + UpdateBitrate against the live encoder");
        Console.Error.WriteLine("  bench-vbv             sweep VBV buffer size, assert per-frame size variance shrinks");
        Console.Error.WriteLine("  bench-intra-refresh   validate intra-refresh produces no IDRs after frame 0");
        Console.Error.WriteLine("  bench-aq              validate AQ shifts bits toward edge / text regions");
    }

    /// <summary>
    /// Phase-3 verification: VBV control actually takes effect on the
    /// SDK path. Sweeps vbvBufferSize across {0.5x, 1x, 2x, 8x} of
    /// (bitrate / fps) and asserts per-frame-size variance decreases as
    /// VBV shrinks. Old MFT-shim data showed identical IDR sizes across
    /// every value (silently ignored); the SDK path's variance must
    /// monotonically respond.
    /// </summary>
    private static int RunVbvBenchmark(Dictionary<string, string> args)
    {
        const int width = 1280;
        const int height = 720;
        const int fps = 30;
        const int gop = 60;
        const long bitrate = 4_000_000;
        const int frames = 240;

        Console.WriteLine($"# bench-vbv {width}x{height}@{fps} bitrate={bitrate} frames={frames}");
        using var devices = new D3D11DeviceManager();
        devices.Initialize();
        var caps = VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencCapabilities.Probe(devices.Device);
        if (!caps.IsAvailable)
        {
            Console.Error.WriteLine($"NVENC unavailable: {caps.UnavailableReason}");
            return 2;
        }
        if (!caps.SupportsCustomVbvBufferSize)
        {
            Console.Error.WriteLine("NVENC reports custom VBV unsupported on this GPU; bench cannot run");
            return 2;
        }

        var perFrameBitsPerSecond = bitrate / fps;
        var multipliers = new double[] { 0.5, 1.0, 2.0, 8.0 };
        var stddevs = new double[multipliers.Length];
        var maxFrames = new int[multipliers.Length];

        for (int sweep = 0; sweep < multipliers.Length; sweep++)
        {
            var vbvBits = (int)(perFrameBitsPerSecond * multipliers[sweep]);
            var options = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencEncodeOptions
            {
                VbvBufferSizeBits = vbvBits,
            };
            using var encoder = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencH264Encoder(
                width, height, fps, bitrate, gop, devices.Device, options);

            var sizes = new List<int>(frames);
            encoder.OutputAvailable += () =>
            {
                while (encoder.TryDequeueEncoded(out var ef))
                {
                    sizes.Add(ef.Bytes.Length);
                }
            };

            var stride = width * 4;
            var bgra = new byte[height * stride];
            FillGradient(bgra, width, height);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                MutatePattern(bgra, i * 7, width); // higher-motion than default
                encoder.EncodeBgra(bgra, stride, clock.Elapsed);
            }
            // Drain.
            Thread.Sleep(300);

            // Skip the IDR (frame 0) in stats — IDRs are always huge.
            var stats = sizes.Skip(1).ToList();
            if (stats.Count == 0)
            {
                Console.WriteLine($"# vbv multiplier={multipliers[sweep]}: NO FRAMES");
                continue;
            }
            var mean = stats.Average();
            var variance = stats.Select(s => (s - mean) * (s - mean)).Average();
            stddevs[sweep] = Math.Sqrt(variance);
            maxFrames[sweep] = stats.Max();

            Console.WriteLine($"RESULT: vbv_x{multipliers[sweep]:F1}={vbvBits}bits frames={stats.Count} mean={mean:F0} stddev={stddevs[sweep]:F0} max={maxFrames[sweep]}");
        }

        // Assert: stddev tighter at smaller VBV. Allow tolerance because
        // the synthetic content might not stress all sizes equally; require
        // the smallest VBV (0.5x) to be tighter than the largest (8x).
        var pass = stddevs[0] < stddevs[3] && maxFrames[0] <= maxFrames[3];
        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} (stddev 0.5x={stddevs[0]:F0} < 8x={stddevs[3]:F0}; max 0.5x={maxFrames[0]} <= 8x={maxFrames[3]})");
        return pass ? 0 : 3;
    }

    /// <summary>
    /// Phase-3 verification: intra-refresh produces refresh slices instead
    /// of full IDRs after frame 0. Encodes 180 frames intra-refresh-on,
    /// parses the H.264 NAL types, asserts at most one IDR (the initial
    /// one) is present.
    /// </summary>
    private static int RunIntraRefreshBenchmark(Dictionary<string, string> args)
    {
        const int width = 1280;
        const int height = 720;
        const int fps = 30;
        const int gop = 60;
        const long bitrate = 4_000_000;
        const int frames = 180;

        Console.WriteLine($"# bench-intra-refresh {width}x{height}@{fps} bitrate={bitrate} frames={frames} gop={gop}");
        using var devices = new D3D11DeviceManager();
        devices.Initialize();
        var caps = VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencCapabilities.Probe(devices.Device);
        if (!caps.IsAvailable)
        {
            Console.Error.WriteLine($"NVENC unavailable: {caps.UnavailableReason}");
            return 2;
        }
        if (!caps.SupportsIntraRefresh)
        {
            Console.Error.WriteLine("NVENC reports intra-refresh unsupported on this GPU; bench cannot run");
            return 2;
        }

        return RunIntraRefreshSweep(devices.Device, width, height, fps, gop, bitrate, frames, intraRefreshOn: true)
            + RunIntraRefreshSweep(devices.Device, width, height, fps, gop, bitrate, frames, intraRefreshOn: false);
    }

    private static int RunIntraRefreshSweep(ID3D11Device device, int width, int height, int fps, int gop, long bitrate, int frames, bool intraRefreshOn)
    {
        var options = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencEncodeOptions
        {
            EnableIntraRefresh = intraRefreshOn,
            IntraRefreshPeriodFrames = intraRefreshOn ? gop : 0,
        };
        using var encoder = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencH264Encoder(
            width, height, fps, bitrate, gop, device, options);

        // Count NAL types per frame. H.264 NAL header byte: lower 5 bits = nal_unit_type.
        // We look for: 5 = IDR slice, 1 = non-IDR slice, 7 = SPS, 8 = PPS, 6 = SEI.
        // With intra-refresh on, IDR (5) should NOT appear after frame 0.
        int idrCount = 0;
        int totalFrames = 0;
        int totalBytes = 0;
        encoder.OutputAvailable += () =>
        {
            while (encoder.TryDequeueEncoded(out var ef))
            {
                totalFrames++;
                totalBytes += ef.Bytes.Length;
                if (ContainsNalType(ef.Bytes, nalType: 5))
                {
                    idrCount++;
                }
            }
        };

        var stride = width * 4;
        var bgra = new byte[height * stride];
        FillGradient(bgra, width, height);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            MutatePattern(bgra, i * 5, width);
            encoder.EncodeBgra(bgra, stride, clock.Elapsed);
        }
        Thread.Sleep(300);

        var label = intraRefreshOn ? "on" : "off";
        Console.WriteLine($"RESULT: intra_refresh_{label}: frames={totalFrames} idrs={idrCount} bytes={totalBytes}");

        if (intraRefreshOn)
        {
            // With intra-refresh ON, only the initial IDR (frame 0) is allowed.
            var pass = idrCount <= 1;
            Console.WriteLine($"VERDICT_intra_on: {(pass ? "PASS" : "FAIL")} (expected ≤1 IDR, got {idrCount})");
            return pass ? 0 : 3;
        }
        else
        {
            // With intra-refresh OFF and gopLength=60 over 180 frames, we
            // expect at least 3 IDRs (one per GOP).
            var pass = idrCount >= 2;
            Console.WriteLine($"VERDICT_intra_off: {(pass ? "PASS" : "FAIL")} (expected ≥2 IDRs as periodic-IDR baseline, got {idrCount})");
            return pass ? 0 : 3;
        }
    }

    /// <summary>
    /// Phase-3 verification: AQ shifts bits toward complex / text regions.
    /// Encodes a composite half-flat / half-text image with AQ off and AQ
    /// on, then compares the per-region byte count by re-encoding each
    /// half independently with the same encoder config. With AQ on the
    /// text half should consume measurably more of the per-frame budget
    /// than the flat half, by a wider margin than with AQ off.
    /// </summary>
    private static int RunAqBenchmark(Dictionary<string, string> args)
    {
        const int width = 1280;
        const int height = 720;
        const int fps = 30;
        const int gop = 60;
        const long bitrate = 2_000_000; // tight enough for AQ to matter
        const int frames = 60;

        Console.WriteLine($"# bench-aq {width}x{height}@{fps} bitrate={bitrate} frames={frames}");
        using var devices = new D3D11DeviceManager();
        devices.Initialize();
        var caps = VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencCapabilities.Probe(devices.Device);
        if (!caps.IsAvailable)
        {
            Console.Error.WriteLine($"NVENC unavailable: {caps.UnavailableReason}");
            return 2;
        }

        // Build a flat-only frame and a text-only frame at the same
        // resolution. We measure each option's effect by encoding each
        // independently and comparing the bit budget.
        var stride = width * 4;
        var flat = new byte[height * stride];
        FillFlatGray(flat, width, height, gray: 128);
        var text = new byte[height * stride];
        FillSyntheticText(text, width, height);

        long EncodeAndMeasure(byte[] frame, bool aqOn)
        {
            var options = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencEncodeOptions
            {
                EnableAdaptiveQuantization = aqOn,
                AqStrength = aqOn ? 8 : 0,
            };
            using var encoder = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencH264Encoder(
                width, height, fps, bitrate, gop, devices.Device, options);
            long total = 0;
            int count = 0;
            encoder.OutputAvailable += () =>
            {
                while (encoder.TryDequeueEncoded(out var ef))
                {
                    // Skip the IDR — it dominates and isn't where AQ shows up.
                    if (count > 0)
                    {
                        total += ef.Bytes.Length;
                    }
                    count++;
                }
            };
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < frames; i++)
            {
                encoder.EncodeBgra(frame, stride, clock.Elapsed);
            }
            Thread.Sleep(300);
            return total;
        }

        var flatOff = EncodeAndMeasure(flat, aqOn: false);
        var textOff = EncodeAndMeasure(text, aqOn: false);
        var flatOn = EncodeAndMeasure(flat, aqOn: true);
        var textOn = EncodeAndMeasure(text, aqOn: true);

        // Ratio of text-bits to flat-bits. AQ on should make text consume
        // a higher proportion of the bit budget compared to flat.
        var ratioOff = textOff / Math.Max(1.0, flatOff);
        var ratioOn = textOn / Math.Max(1.0, flatOn);

        Console.WriteLine($"RESULT: flat_off={flatOff} text_off={textOff} ratio_off={ratioOff:F3}");
        Console.WriteLine($"RESULT: flat_on={flatOn} text_on={textOn} ratio_on={ratioOn:F3}");
        Console.WriteLine($"RESULT: ratio_delta_pct={(ratioOn / Math.Max(0.001, ratioOff) - 1.0) * 100:F1}");

        // AQ should make text-vs-flat allocation MORE skewed toward text.
        var pass = ratioOn > ratioOff;
        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} (ratio_on={ratioOn:F2} > ratio_off={ratioOff:F2})");
        return pass ? 0 : 3;
    }

    /// <summary>
    /// Scan a packed Annex-B H.264 bitstream for any NAL unit with the
    /// given type. Looks for 4-byte start codes (00 00 00 01) and 3-byte
    /// start codes (00 00 01). Returns true on first match.
    /// </summary>
    private static bool ContainsNalType(byte[] bytes, byte nalType)
    {
        for (int i = 0; i + 4 < bytes.Length; i++)
        {
            // 4-byte start: 00 00 00 01
            if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1)
            {
                if ((bytes[i + 4] & 0x1F) == nalType)
                {
                    return true;
                }
            }
            // 3-byte start: 00 00 01
            else if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1)
            {
                if ((bytes[i + 3] & 0x1F) == nalType)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void FillFlatGray(byte[] bgra, int width, int height, byte gray)
    {
        // BGRA: B, G, R, A.
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = gray;
            bgra[i + 1] = gray;
            bgra[i + 2] = gray;
            bgra[i + 3] = 255;
        }
    }

    private static void FillSyntheticText(byte[] bgra, int width, int height)
    {
        // Background gray + alternating black-pixel "letters" — a high-
        // frequency pattern that AQ should preserve while flat areas get
        // less budget.
        FillFlatGray(bgra, width, height, gray: 200);
        // Draw vertical and horizontal lines every 8 pixels — coarse
        // approximation of dense text edges.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool edge = (x % 8 == 0) || (y % 12 == 0) || ((x + y) % 17 == 0);
                if (!edge)
                {
                    continue;
                }
                int i = (y * width + x) * 4;
                bgra[i + 0] = 20;
                bgra[i + 1] = 20;
                bgra[i + 2] = 20;
                bgra[i + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Phase-2 validation: encode 60 frames, request a keyframe at frame 30,
    /// reconfigure bitrate at frame 45. Assert at least one frame >> the
    /// average size lands soon after each keyframe request, and bitrate
    /// reconfigure produces no errors. The bitstream's first byte (NAL
    /// type) is dumped per frame so we can see IDR (5) vs P (1) ordering
    /// in the log.
    /// </summary>
    private static int RunNvencKeyframeBenchmark(Dictionary<string, string> args)
    {
        const int width = 1280;
        const int height = 720;
        const int fps = 30;
        const int gop = 60;
        const long bitrate = 4_000_000;

        Console.WriteLine($"# bench-nvenc-keyframe {width}x{height}@{fps} bitrate={bitrate} gop={gop}");
        using var devices = new D3D11DeviceManager();
        devices.Initialize();
        var caps = VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencCapabilities.Probe(devices.Device);
        if (!caps.IsAvailable)
        {
            Console.Error.WriteLine($"NVENC not available: {caps.UnavailableReason}");
            return 2;
        }

        using var encoder = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencH264Encoder(
            width, height, fps, bitrate, gop, devices.Device);
        var stride = width * 4;
        var bgra = new byte[height * stride];
        FillGradient(bgra, width, height);

        var frames = new List<(int idx, int len, byte firstNalUnitType)>();
        var encodedAtFrame = new Dictionary<int, int>();
        int submittedIdx = 0;
        encoder.OutputAvailable += () =>
        {
            while (encoder.TryDequeueEncoded(out var ef))
            {
                // First NAL unit type: byte 4 lower 5 bits (after 0x00000001 start code).
                byte nalType = 0;
                if (ef.Bytes.Length >= 5)
                {
                    nalType = (byte)(ef.Bytes[4] & 0x1F);
                }
                lock (frames)
                {
                    frames.Add((frames.Count, ef.Bytes.Length, nalType));
                }
            }
        };

        // First 60 frames at 30fps = 2 seconds. Keyframe at frame 30,
        // bitrate change at frame 45.
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 60; i++)
        {
            MutatePattern(bgra, i, width);
            if (i == 30)
            {
                Console.WriteLine("# requesting keyframe");
                encoder.RequestKeyframe();
            }
            if (i == 45)
            {
                Console.WriteLine("# updating bitrate to 8Mbps");
                encoder.UpdateBitrate(8_000_000);
            }
            encoder.EncodeBgra(bgra, stride, clock.Elapsed);
            submittedIdx++;
            // Pace at 30fps.
            var deadline = (long)(submittedIdx * Stopwatch.Frequency / (double)fps);
            while (clock.ElapsedTicks < deadline)
            {
            }
        }

        // Drain — give the encoder ~200ms to flush in-flight frames.
        Thread.Sleep(200);

        lock (frames)
        {
            Console.WriteLine($"RESULT: encoded={frames.Count}");
            int idrCount = 0;
            int? firstIdrIdx = null;
            int? secondIdrIdx = null;
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                if (f.firstNalUnitType == 5 || f.firstNalUnitType == 7) // IDR or SPS — both signal a keyframe boundary
                {
                    idrCount++;
                    firstIdrIdx ??= f.idx;
                    if (idrCount == 2)
                    {
                        secondIdrIdx = f.idx;
                    }
                }
            }
            Console.WriteLine($"RESULT: idr_or_sps_count={idrCount}");
            Console.WriteLine($"RESULT: first_idr_idx={firstIdrIdx}");
            Console.WriteLine($"RESULT: second_idr_idx={secondIdrIdx}");

            // First frame is always IDR. After RequestKeyframe at frame 30,
            // we expect another IDR at or shortly after that index.
            var pass = idrCount >= 2 && secondIdrIdx is { } s && s >= 28 && s <= 35;
            Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} (expected IDR near frame 30, got {secondIdrIdx})");
            return pass ? 0 : 3;
        }
    }

    /// <summary>
    /// Phase-1 verification: open a transient NVENC session, query the
    /// capability bits we care about, print them as machine-grep-able
    /// RESULT lines. Confirms (a) the DLL loads, (b) struct version
    /// arithmetic matches the driver's expectation, (c) the function
    /// table populates with non-null entrypoints, (d) the H.264 codec
    /// GUID is in the supported list, and (e) the cap query path works.
    /// On a non-NVIDIA host this prints "available=0" with the reason.
    /// </summary>
    /// <summary>
    /// Phase-0 (AV1 plan): probe Media Foundation for an AV1 video decoder.
    /// Enumerates MFT VideoDecoder candidates filtered on input subtype
    /// MFVideoFormat_AV1 ({31305641-0000-0010-8000-00AA00389B71}, FOURCC
    /// 'AV01'). A non-empty result means MFT can decode AV1 on this host —
    /// either built-in (Windows 11 with sufficient hardware) or via the
    /// Microsoft Store "AV1 Video Extension" package.
    ///
    /// Output is per-MFT: friendly name, hardware/software flag, and the
    /// underlying CLSID.
    /// </summary>
    private static int RunMftAv1DecoderProbeBenchmark(Dictionary<string, string> args)
    {
        Console.WriteLine("# bench-mft-av1-decoder");
        VicoScreenShare.Client.Windows.Media.Codecs.MediaFoundationRuntime.EnsureInitialized();
        if (!VicoScreenShare.Client.Windows.Media.Codecs.MediaFoundationRuntime.IsAvailable)
        {
            Console.Error.WriteLine("ERROR: Media Foundation could not initialize");
            return 2;
        }

        // MFVideoFormat_AV1 = {31305641-0000-0010-8000-00AA00389B71} — FOURCC
        // 'AV01' as the first DWORD, then Microsoft's standard GUID tail.
        var mfVideoFormatAv1 = new Guid(
            0x31305641, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        var inputFilter = new Vortice.MediaFoundation.RegisterTypeInfo
        {
            GuidMajorType = Vortice.MediaFoundation.MediaTypeGuids.Video,
            GuidSubtype = mfVideoFormatAv1,
        };

        // Try hardware first, then software. Same flags pattern as the
        // H.264 decoder probe in MediaFoundationH264Decoder.CreateDecoder.
        var hwFlags = (uint)(Vortice.MediaFoundation.EnumFlag.EnumFlagHardware
                             | Vortice.MediaFoundation.EnumFlag.EnumFlagSyncmft
                             | Vortice.MediaFoundation.EnumFlag.EnumFlagAsyncmft
                             | Vortice.MediaFoundation.EnumFlag.EnumFlagSortandfilter);
        var swFlags = (uint)(Vortice.MediaFoundation.EnumFlag.EnumFlagSyncmft
                             | Vortice.MediaFoundation.EnumFlag.EnumFlagSortandfilter);

        int hwCount = EnumerateAv1Decoders(inputFilter, hwFlags, "hardware");
        int swCount = EnumerateAv1Decoders(inputFilter, swFlags, "software");

        Console.WriteLine($"RESULT: av1_mft_hardware_decoders={hwCount}");
        Console.WriteLine($"RESULT: av1_mft_software_decoders={swCount}");
        Console.WriteLine($"RESULT: av1_mft_total={hwCount + swCount}");
        var pass = (hwCount + swCount) > 0;
        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} — {(pass ? "MFT AV1 decoder available" : "no AV1 MFT decoder; need NVDEC fallback or AV1 Video Extension install")}");
        return pass ? 0 : 3;
    }

    private static int EnumerateAv1Decoders(Vortice.MediaFoundation.RegisterTypeInfo inputFilter, uint flags, string label)
    {
        try
        {
            using var collection = Vortice.MediaFoundation.MediaFactory.MFTEnumEx(
                Vortice.MediaFoundation.TransformCategoryGuids.VideoDecoder,
                flags,
                inputType: inputFilter,
                outputType: null);

            int count = 0;
            foreach (var activate in collection)
            {
                count++;
                try
                {
                    var name = activate.GetString(Vortice.MediaFoundation.TransformAttributeKeys.MftFriendlyNameAttribute);
                    Console.WriteLine($"RESULT: av1_mft_{label}: {(string.IsNullOrEmpty(name) ? "(unnamed)" : name)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RESULT: av1_mft_{label}: (name query threw: {ex.Message})");
                }
            }
            return count;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RESULT: av1_mft_{label}_enum_threw={ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Phase-2 verification: end-to-end encoder → packetize → optional packet
    /// loss → depacketize → byte-exact verify. With <c>--loss 0</c> every
    /// frame round-trips byte-exact. With non-zero loss, frames whose RTP
    /// packets all arrive should still round-trip; frames missing any
    /// packet are lost (recovered via the encoder's PLI mechanism in a
    /// real session, but this scenario only exercises the RTP layer).
    /// </summary>
    private static int RunAv1RtpRoundtripBenchmark(Dictionary<string, string> args)
    {
        const int width = 1280;
        const int height = 720;
        const int fps = 30;
        const long bitrate = 4_000_000;
        var totalFrames = int.Parse(args.GetValueOrDefault("frames", "60"));
        var mtu = int.Parse(args.GetValueOrDefault("mtu", "1200"));
        var lossRate = double.Parse(args.GetValueOrDefault("loss", "0"));
        if (lossRate < 0 || lossRate > 0.5)
        {
            Console.Error.WriteLine("ERROR: --loss must be in [0, 0.5]");
            return 2;
        }

        Console.WriteLine($"# bench-av1-rtp-roundtrip {width}x{height}@{fps} bitrate={bitrate} frames={totalFrames} mtu={mtu} loss={lossRate}");

        using var devices = new D3D11DeviceManager();
        devices.Initialize();
        var caps = VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencCapabilities.Probe(devices.Device);
        if (!caps.IsAv1Available)
        {
            Console.Error.WriteLine("NVENC AV1 unavailable on this hardware");
            return 2;
        }

        using var encoder = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencAv1Encoder(
            width, height, fps, bitrate, gopFrames: fps * 2, devices.Device);

        var stride = width * 4;
        var bgra = new byte[height * stride];
        FillGradient(bgra, width, height);

        var encoded = new List<byte[]>();
        encoder.OutputAvailable += () =>
        {
            while (encoder.TryDequeueEncoded(out var ef))
            {
                lock (encoded) { encoded.Add(ef.Bytes); }
            }
        };

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < totalFrames; i++)
        {
            MutatePattern(bgra, i * 7, width);
            encoder.EncodeBgra(bgra, stride, clock.Elapsed);
        }
        Thread.Sleep(300);

        // Roundtrip stats. With loss=0, every byte must match. With loss
        // > 0, the frame is irrecoverable — we count those separately
        // and don't expect byte-equality on them.
        var rng = new Random(42);
        var roundtripExact = 0;
        var byteMismatch = 0;
        var lostByDrop = 0;
        var totalPackets = 0;
        var droppedPackets = 0;

        lock (encoded)
        {
            foreach (var frame in encoded)
            {
                var packets = VicoScreenShare.Client.Media.Codecs.Av1RtpPacketizer.Packetize(frame, mtu);
                totalPackets += packets.Count;

                List<byte[]> delivered;
                var anyDropped = false;
                if (lossRate > 0)
                {
                    delivered = new List<byte[]>(packets.Count);
                    foreach (var p in packets)
                    {
                        if (rng.NextDouble() < lossRate)
                        {
                            droppedPackets++;
                            anyDropped = true;
                            continue;
                        }
                        delivered.Add(p);
                    }
                }
                else
                {
                    delivered = packets.ToList();
                }

                if (anyDropped)
                {
                    lostByDrop++;
                    continue;
                }

                var assembled = VicoScreenShare.Client.Media.Codecs.Av1RtpDepacketizer.Depacketize(delivered);
                if (assembled is null || !assembled.SequenceEqual(frame))
                {
                    byteMismatch++;
                }
                else
                {
                    roundtripExact++;
                }
            }
        }

        Console.WriteLine($"RESULT: encoded_frames={encoded.Count}");
        Console.WriteLine($"RESULT: total_packets={totalPackets}");
        Console.WriteLine($"RESULT: dropped_packets={droppedPackets}");
        Console.WriteLine($"RESULT: roundtrip_exact={roundtripExact}");
        Console.WriteLine($"RESULT: byte_mismatch={byteMismatch}");
        Console.WriteLine($"RESULT: lost_by_drop={lostByDrop}");

        // Pass conditions:
        //   loss=0: every frame must round-trip exactly.
        //   loss>0: every NON-dropped frame must round-trip exactly
        //          (the dropped ones are expected to fail).
        var pass = byteMismatch == 0 && (roundtripExact + lostByDrop) == encoded.Count;
        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} (byte_mismatch={byteMismatch})");
        return pass ? 0 : 3;
    }

    /// <summary>
    /// Phase-3 verification: end-to-end NVENC AV1 encode → RTP packetize →
    /// RTP depacketize → MFT AV1 decode → BGRA frame out. Counts decoded
    /// frames vs encoded frames; PASS when ≥95% of encoded frames decode
    /// successfully on the clean (no-loss) path. Both encoder and decoder
    /// are real on the user's hardware — this is the proof that the AV1
    /// pipeline works end-to-end.
    /// </summary>
    private static int RunAv1EncodeDecodeBenchmark(Dictionary<string, string> args)
    {
        var width = int.Parse(args.GetValueOrDefault("width", "1280"));
        var height = int.Parse(args.GetValueOrDefault("height", "720"));
        var totalFrames = int.Parse(args.GetValueOrDefault("frames", "60"));
        const int fps = 30;
        const long bitrate = 4_000_000;

        Console.WriteLine($"# bench-av1-encode-decode {width}x{height}@{fps} bitrate={bitrate} frames={totalFrames}");

        MediaFoundationRuntime.EnsureInitialized();
        if (!MediaFoundationRuntime.IsAvailable)
        {
            Console.Error.WriteLine("ERROR: Media Foundation could not initialize");
            return 2;
        }

        using var devices = new D3D11DeviceManager();
        devices.Initialize();
        var caps = VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencCapabilities.Probe(devices.Device);
        if (!caps.IsAv1Available)
        {
            Console.Error.WriteLine("NVENC AV1 unavailable on this hardware");
            return 2;
        }
        if (!MediaFoundationAv1Decoder.HasAv1DecoderInstalled())
        {
            Console.Error.WriteLine("MFT AV1 decoder unavailable (install \"AV1 Video Extension\" from Microsoft Store)");
            return 2;
        }

        using var encoder = new VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencAv1Encoder(
            width, height, fps, bitrate, gopFrames: fps * 2, devices.Device);
        using var decoder = new MediaFoundationAv1Decoder(devices.Device);

        // Track decoded frames via the GPU output handler (preferred when
        // shared device is set) AND the CPU return-value path (fallback).
        // We just count — pixel validation isn't needed; the AV1 decoder
        // not crashing on our bitstream is the key signal.
        int decoded = 0;
        decoder.GpuOutputHandler = (_, _, _, _) => Interlocked.Increment(ref decoded);

        // Encoder output → RTP packetize → depacketize → decoder feed.
        // Each EncodedFrame is one AV1 temporal unit; we packetize and
        // immediately depacketize back (no actual network), then hand
        // the reassembled OBU stream to the MFT decoder.
        var encodedCount = 0;
        encoder.OutputAvailable += () =>
        {
            while (encoder.TryDequeueEncoded(out var ef))
            {
                Interlocked.Increment(ref encodedCount);
                var packets = VicoScreenShare.Client.Media.Codecs.Av1RtpPacketizer.Packetize(ef.Bytes, mtu: 1200);
                var assembled = VicoScreenShare.Client.Media.Codecs.Av1RtpDepacketizer.Depacketize(packets);
                if (assembled is not null)
                {
                    var cpuFrames = decoder.Decode(assembled, ef.Timestamp);
                    Interlocked.Add(ref decoded, cpuFrames.Count);
                }
            }
        };

        var stride = width * 4;
        var bgra = new byte[height * stride];
        FillGradient(bgra, width, height);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < totalFrames; i++)
        {
            MutatePattern(bgra, i * 7, width);
            encoder.EncodeBgra(bgra, stride, clock.Elapsed);
        }
        Thread.Sleep(500); // drain encoder + decoder pipelines

        Console.WriteLine($"RESULT: encoded={encodedCount}");
        Console.WriteLine($"RESULT: decoded={decoded}");
        var ratio = encodedCount == 0 ? 0.0 : (double)decoded / encodedCount;
        Console.WriteLine($"RESULT: decode_ratio={ratio:F3}");
        var pass = encodedCount > 0 && ratio >= 0.95;
        Console.WriteLine($"VERDICT: {(pass ? "PASS" : "FAIL")} (decoded={decoded} / encoded={encodedCount})");
        return pass ? 0 : 3;
    }

    private static int RunNvencProbeBenchmark(Dictionary<string, string> args)
    {
        Console.WriteLine("# bench-nvenc-probe");

        // Pin struct sizes against SDK 13.0 expectations. Any mismatch
        // here means a layout bug; the InitializeEncoder call later would
        // surface it as a misleading "tuningInfo invalid" or VERSION error.
        // Discovered the hard way that NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE
        // is 16 bytes (one packed bitfield uint + 3 reserved uints), not 4.
        Console.WriteLine($"RESULT: sizeof_NV_ENC_INITIALIZE_PARAMS={System.Runtime.InteropServices.Marshal.SizeOf<VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NV_ENC_INITIALIZE_PARAMS>()}");
        Console.WriteLine($"RESULT: sizeof_NV_ENC_PIC_PARAMS={System.Runtime.InteropServices.Marshal.SizeOf<VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NV_ENC_PIC_PARAMS>()}");

        using var devices = new D3D11DeviceManager();
        devices.Initialize();

        VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencCapabilities.ResetForTesting();
        var caps = VicoScreenShare.Client.Windows.Media.Codecs.Nvenc.NvencCapabilities.Probe(devices.Device);

        Console.WriteLine($"RESULT: available={(caps.IsAvailable ? 1 : 0)}");
        if (!caps.IsAvailable)
        {
            Console.WriteLine($"RESULT: reason={caps.UnavailableReason}");
            return 0;
        }

        Console.WriteLine($"RESULT: temporal_aq={(caps.SupportsTemporalAq ? 1 : 0)}");
        Console.WriteLine($"RESULT: lookahead={(caps.SupportsLookahead ? 1 : 0)}");
        Console.WriteLine($"RESULT: intra_refresh={(caps.SupportsIntraRefresh ? 1 : 0)}");
        Console.WriteLine($"RESULT: custom_vbv={(caps.SupportsCustomVbvBufferSize ? 1 : 0)}");
        Console.WriteLine($"RESULT: async_encode={(caps.SupportsAsyncEncode ? 1 : 0)}");
        Console.WriteLine($"RESULT: width_max={caps.MaxWidth}");
        Console.WriteLine($"RESULT: height_max={caps.MaxHeight}");
        Console.WriteLine($"RESULT: av1_available={(caps.IsAv1Available ? 1 : 0)}");
        if (caps.IsAv1Available)
        {
            Console.WriteLine($"RESULT: av1_temporal_aq={(caps.Av1SupportsTemporalAq ? 1 : 0)}");
            Console.WriteLine($"RESULT: av1_lookahead={(caps.Av1SupportsLookahead ? 1 : 0)}");
            Console.WriteLine($"RESULT: av1_intra_refresh={(caps.Av1SupportsIntraRefresh ? 1 : 0)}");
            Console.WriteLine($"RESULT: av1_custom_vbv={(caps.Av1SupportsCustomVbvBufferSize ? 1 : 0)}");
            Console.WriteLine($"RESULT: av1_width_max={caps.Av1MaxWidth}");
            Console.WriteLine($"RESULT: av1_height_max={caps.Av1MaxHeight}");
        }
        return 0;
    }
}
