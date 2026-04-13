using System;
using System.Collections.Generic;
using System.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Windows.Media.Codecs;

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

        var encoder = factory.CreateEncoder(width, height, fps, bitrate);
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
        for (var w = 0; w < 5; w++)
        {
            MutatePattern(bgra, w, width);
            _ = encoder.EncodeBgra(bgra, stride);
        }

        var benchSw = Stopwatch.StartNew();
        var startTicks = totalSw.ElapsedTicks;
        var iteration = 0;

        while (totalSw.ElapsedTicks - startTicks < endTicks)
        {
            MutatePattern(bgra, iteration + 100, width);

            var encStart = Stopwatch.GetTimestamp();
            var encoded = encoder.EncodeBgra(bgra, stride);
            var encEnd = Stopwatch.GetTimestamp();

            var encodeMs = (encEnd - encStart) * 1000.0 / Stopwatch.Frequency;
            encodeMsTotal += encodeMs;
            if (encodeMs > encodeMsMax) encodeMsMax = encodeMs;
            if (encodeMs < encodeMsMin) encodeMsMin = encodeMs;

            if (encoded is { Length: > 0 })
            {
                frameCount++;
                totalBytes += encoded.Length;
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
