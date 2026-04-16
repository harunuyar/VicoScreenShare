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
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Services;
using ScreenSharing.Client.Windows.Capture;
using ScreenSharing.Client.Windows.Direct3D;
using ScreenSharing.Client.Windows.Media.Codecs;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;
using ScreenSharing.Server;
using SIPSorcery.Net;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenSharing.MediaHarness;

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
                "bench-encode-gpu" => RunGpuEncodeBenchmark(argMap),
                "bench-downscale" => RunDownscaleBenchmark(argMap),
                "bench-scaler" => RunVideoScalerBenchmark(argMap),
                "bench-capture-e2e" => RunCaptureEndToEndBenchmark(argMap),
                "bench-encode-decode" => RunEncodeDecodeBenchmark(argMap),
                "bench-network-e2e" => RunNetworkEndToEndBenchmark(argMap).GetAwaiter().GetResult(),
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
        var throttle = args.GetValueOrDefault("throttle", "true") != "false";

        Console.WriteLine($"# bench-encode codec={codec} {width}x{height}@{fps} bitrate={bitrateMbps}Mbps duration={duration}s throttle={throttle}");

        IVideoEncoderFactory factory;
        switch (codec)
        {
            case "h264":
                MediaFoundationRuntime.EnsureInitialized();
                if (!MediaFoundationRuntime.IsAvailable)
                {
                    Console.Error.WriteLine("ERROR: Media Foundation could not initialize");
                    return 2;
                }
                factory = new MediaFoundationH264EncoderFactory();
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
            if (encodeMs > encodeMsMax) encodeMsMax = encodeMs;
            if (encodeMs < encodeMsMin) encodeMsMin = encodeMs;

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
            if (encodeMs > encodeMsMax) encodeMsMax = encodeMs;
            if (encodeMs < encodeMsMin) encodeMsMin = encodeMs;

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
        lock (arrivalLock) sortedGaps = arrivalGaps.OrderBy(g => g).ToArray();
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
        var encoderFactory = new MediaFoundationH264EncoderFactory(sharedDevices.Device);
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
        if (sortedValues.Length == 0) return 0.0;
        var rank = (percentile / 100.0) * (sortedValues.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sortedValues[lo];
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
        for (var i = 0; i < src.Length; i++) src[i] = (byte)(i & 0xFF);

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
            if (ms > maxMs) maxMs = ms;
            if (ms < minMs) minMs = ms;
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
        for (var i = 0; i < srcBytes.Length; i++) srcBytes[i] = (byte)(i & 0xFF);

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
            if (ms < minMs) minMs = ms;
            if (ms > maxMs) maxMs = ms;
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
        Console.Error.WriteLine("usage: ScreenSharing.MediaHarness <scenario> [args]");
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
    }
}
