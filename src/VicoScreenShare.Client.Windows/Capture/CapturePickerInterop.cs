namespace VicoScreenShare.Client.Windows.Capture;

using System;

/// <summary>
/// Parents a WinRT picker (like <c>GraphicsCapturePicker</c>) to a Win32 HWND. The
/// canonical CsWinRT helper for this is <see cref="WinRT.Interop.InitializeWithWindow"/>
/// — trying to cast a WinRT projected type to a hand-rolled <c>[ComImport]</c>
/// <c>IInitializeWithWindow</c> throws <c>InvalidCastException</c> because CsWinRT
/// projections and classic COM interop types live in separate worlds.
/// </summary>
internal static class CapturePickerInterop
{
    public static void InitializeWithWindow(object picker, IntPtr hwnd)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("Window handle is zero.", nameof(hwnd));
        }

        global::WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
