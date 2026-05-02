namespace VicoScreenShare.Client.Diagnostics;

using System;
using System.Diagnostics;
using System.IO;

/// <summary>
/// Tiny file-backed logger for debug-only output. Appends to a single
/// shared file at <c>%TEMP%/screensharing/debug.log</c> with a per-line
/// process-id prefix so multiple instances on the same machine
/// (publisher + receiver running side by side, or two viewers) all
/// land in one chronological timeline. A tester sends one file; we
/// filter by pid when we want one process's view.
///
/// <para>
/// File is never wiped at startup — that wiped the OTHER instance's
/// logs when both ran on the same box, which is exactly the case where
/// cross-process diagnostics matter most. Instead, at process startup
/// we check the file's size and rotate (rename to <c>debug.1.log</c>,
/// cascade older rotations one slot back) when it exceeds
/// <see cref="MaxBytes"/>. Old rotations beyond <see cref="KeepRotations"/>
/// are discarded.
/// </para>
///
/// <para>
/// Within a process, writes are serialized through <see cref="Gate"/>
/// to avoid interleaving partial lines. Across processes there's no
/// cross-process lock — both <see cref="File.AppendAllText"/> calls
/// open/append/close, the OS atomic append on small writes keeps lines
/// intact in practice, and any rare race just produces an
/// out-of-order or dropped line which the surrounding pulse loggers
/// will recover from on the next cycle. The <c>catch</c> block below
/// guarantees we never crash the caller for a logging failure.
/// </para>
/// </summary>
public static class DebugLog
{
    /// <summary>Max size of <c>debug.log</c> before startup rotation kicks in.</summary>
    private const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>How many rotated files to keep (debug.1.log .. debug.N.log).</summary>
    private const int KeepRotations = 3;

    private static readonly object Gate = new();
    private static readonly string Path = TempPaths.Combine("debug.log");
    private static readonly int Pid = Process.GetCurrentProcess().Id;

    public static string FilePath => Path;

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(
                    Path,
                    $"{DateTime.Now:HH:mm:ss.fff} pid={Pid} {message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostic must never crash the caller.
            }
        }
    }

    /// <summary>
    /// Startup-time housekeeping: if <c>debug.log</c> is bigger than
    /// <see cref="MaxBytes"/>, rotate it (rename to <c>debug.1.log</c>;
    /// older rotations cascade to <c>.2</c>, <c>.3</c>, etc; rotation
    /// past <see cref="KeepRotations"/> is dropped). Fresh log file is
    /// created on the next <see cref="Write"/>. If another instance is
    /// running and currently writing, the rotation may race and skip;
    /// that's fine — next startup will catch it.
    ///
    /// Replaces the old <c>Reset()</c> behavior, which deleted the file
    /// outright and wiped every other co-resident process's logs.
    /// Callers who used to call <c>Reset</c> at app startup should call
    /// this instead.
    /// </summary>
    public static void RotateIfOversized()
    {
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(Path);
                if (!info.Exists || info.Length < MaxBytes)
                {
                    return;
                }

                // Cascade existing rotations: drop the oldest, shift
                // each older slot one back. e.g. debug.2.log -> debug.3.log,
                // debug.1.log -> debug.2.log.
                for (var i = KeepRotations; i >= 1; i--)
                {
                    var src = i == 1 ? Path : RotationPath(i - 1);
                    var dst = RotationPath(i);
                    if (!File.Exists(src))
                    {
                        continue;
                    }
                    if (i == KeepRotations)
                    {
                        try { File.Delete(dst); } catch { }
                    }
                    try { File.Move(src, dst, overwrite: true); }
                    catch { /* race with another instance — skip this slot */ }
                }
            }
            catch
            {
                // Diagnostic must never crash startup.
            }
        }
    }

    private static string RotationPath(int index)
        => System.IO.Path.ChangeExtension(Path, $"{index}.log");
}
