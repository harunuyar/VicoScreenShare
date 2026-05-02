namespace VicoScreenShare.Client.Diagnostics;

using System;
using System.IO;

/// <summary>
/// Crash-resume sentinel for the NVDEC AV1 / H.264 decoder constructors.
///
/// Why this exists: <c>cuvidCreateVideoParser</c> and <c>cuvidCreateDecoder</c>
/// are P/Invoke calls into NVIDIA's native cuvid library. They occasionally
/// access-violate (process-fatal) on a fraction of receiver constructions
/// — observed as "first attempt crashes, retry works" in the field. A
/// native AV cannot be caught by <c>AppDomain.UnhandledException</c> or
/// <c>DispatcherUnhandledException</c> in .NET 5+, so we cannot wrap the
/// call in a try/catch and fall back at runtime. Instead we work around
/// it cooperatively across launches:
///
/// <list type="number">
///   <item>Before calling into cuvid, write a sentinel file
///     (<see cref="MarkAttempt"/>).</item>
///   <item>After the call succeeds, delete the sentinel
///     (<see cref="ClearAttempt"/>).</item>
///   <item>If the process crashes between mark and clear, the file
///     persists past the crash. Next launch checks it via
///     <see cref="WasLastAttemptCrashed"/>: if set, the
///     <c>Av1DecoderFactorySelector</c> / <c>H264DecoderFactorySelector</c>
///     forces the MFT path for this session and clears the sentinel so
///     the launch after that retries NVDEC normally.</item>
/// </list>
///
/// This means a single native AV costs the user one MFT-only session,
/// not a permanent NVDEC opt-out. The trade-off matches the behavior:
/// the crash is intermittent, not deterministic — so a one-session
/// fallback is the right granularity.
/// </summary>
public static class NvdecCrashSentinel
{
    private static string SentinelPath(string codecTag) => TempPaths.Combine($".nvdec-{codecTag}-attempt");

    /// <summary>Returns true when the previous launch left a mark for
    /// this codec — i.e., <see cref="MarkAttempt"/> was called and
    /// <see cref="ClearAttempt"/> never followed because the process
    /// crashed in the cuvid call.</summary>
    public static bool WasLastAttemptCrashed(string codecTag)
    {
        try { return File.Exists(SentinelPath(codecTag)); }
        catch { return false; }
    }

    /// <summary>Drop a sentinel file recording that we are about to
    /// enter a cuvid call known to occasionally AV. The file's content
    /// is the timestamp; the file's existence is what matters.</summary>
    public static void MarkAttempt(string codecTag)
    {
        try { File.WriteAllText(SentinelPath(codecTag), DateTime.UtcNow.ToString("o")); }
        catch { /* sentinel is advisory — never crash diagnostics */ }
    }

    /// <summary>Clear the sentinel after a successful cuvid call. Safe
    /// to call when no sentinel exists.</summary>
    public static void ClearAttempt(string codecTag)
    {
        try { File.Delete(SentinelPath(codecTag)); }
        catch { /* never crash diagnostics */ }
    }
}
