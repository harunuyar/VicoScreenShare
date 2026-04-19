namespace VicoScreenShare.MediaHarness;

using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

/// <summary>
/// Gets a <see cref="GraphicsCaptureItem"/> for the primary monitor without
/// showing the system picker, so the harness can drive the capture pipeline
/// end-to-end in a headless-ish scenario. Same IGraphicsCaptureItemInterop
/// dance robmikh's Win32CaptureSample uses.
/// </summary>
internal static class MonitorCaptureItem
{
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        int CreateForWindow([In] IntPtr window, [In] ref Guid iid, out IntPtr result);
        int CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid, out IntPtr result);
    }

    public static GraphicsCaptureItem CreateForPrimaryMonitor()
    {
        var hmon = MonitorFromPoint(new POINT { x = 0, y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (hmon == IntPtr.Zero)
        {
            throw new InvalidOperationException("MonitorFromPoint returned null.");
        }

        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
        var hr = factory.CreateForMonitor(hmon, ref iidItem, out var itemPtr);
        if (hr != 0 || itemPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateForMonitor failed HR=0x{hr:X8}");
        }

        return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
    }

    public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("hwnd is zero", nameof(hwnd));
        }

        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var hr = factory.CreateForWindow(hwnd, ref iidItem, out var itemPtr);
        if (hr != 0 || itemPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateForWindow failed HR=0x{hr:X8}");
        }

        return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
    }
}
