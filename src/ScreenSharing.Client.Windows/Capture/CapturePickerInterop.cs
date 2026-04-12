using System;
using System.Runtime.InteropServices;

namespace ScreenSharing.Client.Windows.Capture;

/// <summary>
/// COM glue for handing a WinRT picker (like <c>GraphicsCapturePicker</c>) an HWND
/// so it can parent its modal dialog correctly from a non-UWP desktop app.
/// </summary>
internal static class CapturePickerInterop
{
    public static void InitializeWithWindow(object picker, IntPtr hwnd)
    {
        if (picker is null) throw new ArgumentNullException(nameof(picker));
        if (hwnd == IntPtr.Zero) throw new ArgumentException("Window handle is zero.", nameof(hwnd));

        var withWindow = (IInitializeWithWindow)picker;
        withWindow.Initialize(hwnd);
    }

    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithWindow
    {
        void Initialize(IntPtr hwnd);
    }
}
