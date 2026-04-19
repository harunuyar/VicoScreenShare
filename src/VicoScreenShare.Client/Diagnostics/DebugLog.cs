namespace VicoScreenShare.Client.Diagnostics;

using System;
using System.IO;

/// <summary>
/// Tiny file-backed logger for debug-only output. The Desktop.Windows host is
/// a <c>WinExe</c> and detaches from the parent console at startup, so plain
/// <c>Console.WriteLine</c> calls vanish when the app is launched from a
/// terminal. This writer appends lines to
/// <c>%TEMP%/screensharing/debug.log</c> (resolved via <see cref="TempPaths"/>)
/// and flushes every write so users can just open the file and paste the
/// contents when we ask for diagnostic data.
/// </summary>
public static class DebugLog
{
    private static readonly object Gate = new();
    private static readonly string Path = TempPaths.Combine("debug.log");

    public static string FilePath => Path;

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(
                    Path,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostic must never crash the caller.
            }
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
