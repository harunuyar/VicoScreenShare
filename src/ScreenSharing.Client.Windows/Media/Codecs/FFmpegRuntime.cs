using System;
using System.IO;
using ScreenSharing.Client.Diagnostics;
using SIPSorceryMedia.FFmpeg;

namespace ScreenSharing.Client.Windows.Media.Codecs;

/// <summary>
/// Owns the one-shot FFmpeg native-library registration and exposes the result
/// as an <see cref="IsAvailable"/> flag. SIPSorceryMedia.FFmpeg 8.0.12 is
/// pinned against FFmpeg.AutoGen 7.0, which expects FFmpeg 7.x shared
/// libraries (<c>avcodec-61.dll</c>, <c>avformat-61.dll</c>, etc.) reachable
/// via one of:
///   1. the path passed explicitly to <see cref="EnsureInitialized"/>
///   2. the <c>FFMPEG_BINARIES_PATH</c> environment variable
///   3. the process PATH (winget / chocolatey installs usually land here)
///   4. a <c>tools/ffmpeg/bin</c> folder next to the app (for portable drops)
///
/// When none of those work, <see cref="IsAvailable"/> stays false and the
/// H.264 / future AV1 factories report themselves unavailable so the settings
/// UI can gray them out with a helpful message.
/// </summary>
public static class FFmpegRuntime
{
    private static readonly object _lock = new();
    private static bool _attempted;
    private static bool _available;
    private static string? _lastError;

    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _available;
        }
    }

    public static string? LastError
    {
        get
        {
            EnsureInitialized();
            return _lastError;
        }
    }

    public static void EnsureInitialized(string? explicitPath = null)
    {
        lock (_lock)
        {
            if (_attempted) return;
            _attempted = true;

            foreach (var candidate in EnumerateCandidatePaths(explicitPath))
            {
                try
                {
                    FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, candidate);
                    _available = true;
                    _lastError = null;
                    DebugLog.Write($"[ffmpeg] initialized from '{candidate ?? "<default search>"}'");
                    return;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    DebugLog.Write($"[ffmpeg] init failed for '{candidate ?? "<default search>"}': {ex.Message}");
                }
            }

            _available = false;
        }
    }

    private static System.Collections.Generic.IEnumerable<string?> EnumerateCandidatePaths(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath;
        }

        var envPath = Environment.GetEnvironmentVariable("FFMPEG_BINARIES_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            yield return envPath;
        }

        // Portable drop: tools/ffmpeg/bin next to the application.
        var baseDir = AppContext.BaseDirectory;
        var portable = Path.Combine(baseDir, "tools", "ffmpeg", "bin");
        if (Directory.Exists(portable))
        {
            yield return portable;
        }

        // Null = let SIPSorcery fall back to PATH and its own heuristics.
        yield return null;
    }
}
