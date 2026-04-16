using System;
using System.IO;

namespace ScreenSharing.Client.Diagnostics;

/// <summary>
/// Single source of truth for the project's temp scratch directory.
/// Everything we drop in <c>%TEMP%</c> (debug log, frame dumps, future
/// diagnostic captures) goes under <c>%TEMP%/screensharing/</c> so it's
/// trivial to find and clean up. The directory is created on first use.
/// </summary>
public static class TempPaths
{
    private static readonly Lazy<string> _root = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "screensharing");
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // If creation fails (very unusual), fall back to %TEMP% so the
            // diagnostic writers still have somewhere to land.
            path = Path.GetTempPath();
        }
        return path;
    });

    public static string RootDir => _root.Value;

    public static string Combine(string fileName) => Path.Combine(RootDir, fileName);
}
