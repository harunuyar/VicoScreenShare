namespace VicoScreenShare.Desktop.App;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VicoScreenShare.Client.Diagnostics;

/// <summary>
/// Opts this process out of Windows background-process throttling.
///
/// Why this matters: Windows 10+ applies two independent throttles to apps
/// that aren't the foreground window:
///  1. <b>Power Throttling</b> — the process is scheduled onto efficiency
///     cores (on hybrid CPUs) and has its per-thread quantum shortened.
///  2. <b>Process priority drop</b> — WPF apps in the background get their
///     dispatcher timer resolution and thread-pool priority reduced.
///
/// For a real-time video app this is catastrophic: the SIPSorcery UDP
/// receive thread stops draining the kernel socket fast enough, the kernel
/// queue fills, and packets silently drop — *without* showing up in any
/// standard UDP error counter, because the drops happen inside the socket
/// tier rather than at protocol parse. Diagnosed live as ~70% packet loss
/// on backgrounded subscriber windows while the foreground window had 0%
/// loss, same machine, same NIC, same wire.
///
/// Fix is two system calls at startup:
///  - <see cref="SetProcessInformation"/> with
///    <c>PROCESS_POWER_THROTTLING_STATE</c>, StateMask=0 → "never throttle."
///  - <see cref="Process.PriorityClass"/> = <see cref="ProcessPriorityClass.AboveNormal"/>
///    → keeps thread scheduling quanta full-length even in background.
///
/// Both are best-effort; failures are logged and ignored (the app keeps
/// running, just with the old throttling behavior). This is the same
/// fix Teams, Discord, Zoom and OBS apply to survive being minimized.
/// </summary>
internal static class BackgroundThrottlingOptOut
{
    // From processthreadsapi.h — Win10 1709+ API for per-process power
    // throttling opt-out. Not declared in .NET BCL; we P/Invoke it.
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        uint ProcessInformationSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static void Apply()
    {
        TryDisablePowerThrottling();
        TrySetPriority();
    }

    private static void TryDisablePowerThrottling()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                // StateMask = 0 means "turn throttling OFF for the masked
                // controls" — i.e. execution speed is never throttled.
                StateMask = 0,
            };
            var ok = SetProcessInformation(
                GetCurrentProcess(),
                ProcessPowerThrottling,
                ref state,
                (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            if (ok)
            {
                DebugLog.Write("[throttle] power throttling disabled for this process");
            }
            else
            {
                DebugLog.Write($"[throttle] SetProcessInformation returned false, LastError={Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[throttle] power-throttling opt-out threw: {ex.Message}");
        }
    }

    private static void TrySetPriority()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.PriorityClass = ProcessPriorityClass.AboveNormal;
            DebugLog.Write($"[throttle] process priority set to AboveNormal");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[throttle] priority set threw: {ex.Message}");
        }
    }
}
