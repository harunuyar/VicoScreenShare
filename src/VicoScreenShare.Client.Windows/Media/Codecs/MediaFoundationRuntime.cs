using System;
using VicoScreenShare.Client.Diagnostics;
using Vortice.MediaFoundation;

namespace VicoScreenShare.Client.Windows.Media.Codecs;

/// <summary>
/// Owns the one-shot <see cref="MediaFactory.MFStartup"/> call that every MF
/// consumer in the process needs. Safe to call <see cref="EnsureInitialized"/>
/// multiple times — only the first call runs, and the result is cached.
///
/// Media Foundation ships with every Windows 10+ install, so the only reason
/// this would fail is if the process was denied access (MTA threading issues,
/// policy restrictions on server SKUs) — in which case <see cref="IsAvailable"/>
/// stays false and the factory layer reports H.264 as unavailable rather than
/// crashing the app at codec-selection time.
/// </summary>
public static class MediaFoundationRuntime
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

    public static void EnsureInitialized()
    {
        lock (_lock)
        {
            if (_attempted) return;
            _attempted = true;

            try
            {
                var result = MediaFactory.MFStartup();
                if (result.Failure)
                {
                    _lastError = $"MFStartup HRESULT 0x{(uint)result.Code:X8}";
                    DebugLog.Write($"[mf] MFStartup failed: {_lastError}");
                    return;
                }
                _available = true;
                DebugLog.Write("[mf] MFStartup succeeded");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                DebugLog.Write($"[mf] MFStartup threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
