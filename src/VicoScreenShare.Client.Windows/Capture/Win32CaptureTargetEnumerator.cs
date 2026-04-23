namespace VicoScreenShare.Client.Windows.Capture;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Platform;

/// <summary>
/// Win32-backed <see cref="ICaptureTargetEnumerator"/>. Lists top-level
/// windows that are visible and rectangular (filters out cloaked /
/// invisible / tool-only windows Discord and OBS also hide) plus every
/// physical monitor. Icon is pulled per-window at enumeration time
/// (cheap, <c>WM_GETICON</c> / <c>GCLP_HICON</c>); thumbnail is lazy —
/// <see cref="GetThumbnailAsync"/> grabs a fresh frame via
/// <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c> so DWM-composed
/// windows (modern browsers, UWP apps) render correctly.
/// </summary>
public sealed class Win32CaptureTargetEnumerator : ICaptureTargetEnumerator
{
    public Task<IReadOnlyList<CaptureTarget>> EnumerateAsync(CancellationToken ct = default)
    {
        var list = new List<CaptureTarget>(64);
        var ownPid = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsShareableWindow(hwnd, ownPid))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            var ownerName = GetProcessFriendlyName((int)pid);
            var icon = TryGetWindowIcon(hwnd);

            list.Add(new CaptureTarget(
                CaptureTargetKind.Window,
                displayName: title,
                ownerDisplayName: ownerName,
                handle: hwnd,
                processId: (int)pid,
                icon: icon));
            return true;
        }, IntPtr.Zero);

        // Monitors come after windows so the picker shows "Windows" first —
        // matches Discord's ordering. Users who want a full-screen share
        // scroll to the bottom.
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMonitor, ref info))
            {
                return true;
            }
            var isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
            var displayLabel = string.IsNullOrEmpty(info.szDevice)
                ? "Display"
                : info.szDevice.TrimStart('\\', '.');
            var title = isPrimary ? $"{displayLabel} (primary)" : displayLabel;
            var resolution = $"{info.rcMonitor.right - info.rcMonitor.left} × {info.rcMonitor.bottom - info.rcMonitor.top}";

            list.Add(new CaptureTarget(
                CaptureTargetKind.Monitor,
                displayName: title,
                ownerDisplayName: resolution,
                handle: hMonitor,
                processId: 0,
                icon: null));
            return true;
        }, IntPtr.Zero);

        return Task.FromResult<IReadOnlyList<CaptureTarget>>(list);
    }

    public Task<CaptureTargetImage?> GetThumbnailAsync(CaptureTarget target, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        if (target.Handle == IntPtr.Zero || maxWidth <= 0 || maxHeight <= 0)
        {
            return Task.FromResult<CaptureTargetImage?>(null);
        }

        return target.Kind switch
        {
            CaptureTargetKind.Window => Task.FromResult(CaptureWindowThumbnail(target.Handle, maxWidth, maxHeight)),
            CaptureTargetKind.Monitor => Task.FromResult(CaptureMonitorThumbnail(target.Handle, maxWidth, maxHeight)),
            _ => Task.FromResult<CaptureTargetImage?>(null),
        };
    }

    // ---------------- Window filter + metadata ----------------

    private static bool IsShareableWindow(IntPtr hwnd, int ownPid)
    {
        if (!IsWindowVisible(hwnd))
        {
            return false;
        }
        if (IsIconic(hwnd))
        {
            // Minimized windows produce black thumbnails via PrintWindow;
            // skip them in the picker. User can restore the window and
            // re-open the picker to share it.
            return false;
        }

        // Filter cloaked windows (UWP apps that are "running in the
        // background" report visible but invisible to the compositor —
        // classic false positive on EnumWindows).
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloakedFlag, sizeof(int)) == 0 && cloakedFlag != 0)
        {
            return false;
        }

        // No tool windows without a title (tooltips, dropdowns).
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        // Skip zero-size windows and anything smaller than a typical
        // tooltip — nothing worth sharing is <120×60.
        if (!GetWindowRect(hwnd, out var rect))
        {
            return false;
        }
        if (rect.right - rect.left < 120 || rect.bottom - rect.top < 60)
        {
            return false;
        }

        // Skip our own windows so the user doesn't pick the picker /
        // main window and create a recursive capture.
        GetWindowThreadProcessId(hwnd, out var pid);
        if ((int)pid == ownPid)
        {
            return false;
        }

        return true;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len == 0)
        {
            return string.Empty;
        }
        Span<char> buffer = stackalloc char[Math.Min(len + 1, 512)];
        unsafe
        {
            fixed (char* p = buffer)
            {
                var read = GetWindowText(hwnd, p, buffer.Length);
                return new string(p, 0, read);
            }
        }
    }

    private static string GetProcessFriendlyName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                return Path.GetFileNameWithoutExtension(path);
            }
            return process.ProcessName;
        }
        catch
        {
            // Access denied on protected processes (antivirus, some
            // system apps). Fall back to PID for the label so the
            // picker still shows something recognizable.
            return $"pid {pid}";
        }
    }

    // ---------------- Icons ----------------

    private static CaptureTargetImage? TryGetWindowIcon(IntPtr hwnd)
    {
        // Priority order matches Explorer: try ICON_SMALL2 (high-DPI
        // variant), then ICON_SMALL, then the class icon, then the
        // BIG icon scaled down. Most modern apps populate ICON_SMALL2.
        var hicon = SendMessageIcon(hwnd, WM_GETICON, ICON_SMALL2, IntPtr.Zero);
        if (hicon == IntPtr.Zero)
        {
            hicon = SendMessageIcon(hwnd, WM_GETICON, ICON_SMALL, IntPtr.Zero);
        }
        if (hicon == IntPtr.Zero)
        {
            hicon = GetClassLongPtr(hwnd, GCLP_HICONSM);
        }
        if (hicon == IntPtr.Zero)
        {
            hicon = GetClassLongPtr(hwnd, GCLP_HICON);
        }
        if (hicon == IntPtr.Zero)
        {
            hicon = SendMessageIcon(hwnd, WM_GETICON, ICON_BIG, IntPtr.Zero);
        }

        return hicon == IntPtr.Zero ? null : IconToBgra(hicon);
    }

    private static CaptureTargetImage? IconToBgra(IntPtr hicon)
    {
        try
        {
            if (!GetIconInfo(hicon, out var info))
            {
                return null;
            }

            try
            {
                if (info.hbmColor == IntPtr.Zero)
                {
                    return null;
                }

                var bm = new BITMAP();
                GetObject(info.hbmColor, Marshal.SizeOf<BITMAP>(), ref bm);
                var width = bm.bmWidth;
                var height = bm.bmHeight;
                if (width <= 0 || height <= 0 || width > 512 || height > 512)
                {
                    return null;
                }

                var stride = width * 4;
                var pixels = new byte[stride * height];
                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = width,
                        // Negative height = top-down DIB (origin at top-left,
                        // matches our BGRA contract).
                        biHeight = -height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB,
                    },
                };

                var hdc = GetDC(IntPtr.Zero);
                try
                {
                    unsafe
                    {
                        fixed (byte* dest = pixels)
                        {
                            GetDIBits(hdc, info.hbmColor, 0, (uint)height, (IntPtr)dest, ref bmi, DIB_RGB_COLORS);
                        }
                    }
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, hdc);
                }

                return new CaptureTargetImage(pixels, width, height, stride);
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero)
                {
                    DeleteObject(info.hbmColor);
                }
                if (info.hbmMask != IntPtr.Zero)
                {
                    DeleteObject(info.hbmMask);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    // ---------------- Thumbnails ----------------

    private static CaptureTargetImage? CaptureWindowThumbnail(IntPtr hwnd, int maxWidth, int maxHeight)
    {
        if (!GetClientRect(hwnd, out var rect))
        {
            return null;
        }
        var srcW = rect.right - rect.left;
        var srcH = rect.bottom - rect.top;
        if (srcW <= 0 || srcH <= 0)
        {
            return null;
        }

        return CaptureWithPrintWindow(hwnd, srcW, srcH, maxWidth, maxHeight, monitor: false);
    }

    private static CaptureTargetImage? CaptureMonitorThumbnail(IntPtr hMonitor, int maxWidth, int maxHeight)
    {
        var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMonitor, ref info))
        {
            return null;
        }
        var srcW = info.rcMonitor.right - info.rcMonitor.left;
        var srcH = info.rcMonitor.bottom - info.rcMonitor.top;
        if (srcW <= 0 || srcH <= 0)
        {
            return null;
        }
        return CaptureFromScreen(info.rcMonitor.left, info.rcMonitor.top, srcW, srcH, maxWidth, maxHeight);
    }

    private static CaptureTargetImage? CaptureWithPrintWindow(IntPtr hwnd, int srcW, int srcH, int maxWidth, int maxHeight, bool monitor)
    {
        // Fit the source into the requested box preserving aspect ratio.
        var (dstW, dstH) = FitInside(srcW, srcH, maxWidth, maxHeight);
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        IntPtr srcDc = IntPtr.Zero;
        IntPtr srcBitmap = IntPtr.Zero;
        IntPtr scaledDc = IntPtr.Zero;
        IntPtr scaledBitmap = IntPtr.Zero;

        try
        {
            srcDc = CreateCompatibleDC(screenDc);
            srcBitmap = CreateCompatibleBitmap(screenDc, srcW, srcH);
            if (srcDc == IntPtr.Zero || srcBitmap == IntPtr.Zero)
            {
                return null;
            }
            var prevSrc = SelectObject(srcDc, srcBitmap);
            try
            {
                // PW_RENDERFULLCONTENT (0x2) is required for DWM-composed
                // windows (Chrome, Edge, any UWP host) — without it they
                // paint as a black rectangle. Added in Windows 8.1.
                if (!PrintWindow(hwnd, srcDc, PW_RENDERFULLCONTENT))
                {
                    // Fallback: plain PrintWindow without RENDERFULLCONTENT.
                    // Some classic Win32 apps only respond to the simpler
                    // WM_PRINT path; the DWM flag makes them paint black.
                    if (!PrintWindow(hwnd, srcDc, 0))
                    {
                        DebugLog.Write($"[picker] PrintWindow failed for hwnd=0x{hwnd.ToInt64():X}");
                        return null;
                    }
                }
            }
            finally
            {
                SelectObject(srcDc, prevSrc);
            }

            // Now downscale srcBitmap into a dst-sized bitmap via StretchBlt.
            scaledDc = CreateCompatibleDC(screenDc);
            scaledBitmap = CreateCompatibleBitmap(screenDc, dstW, dstH);
            if (scaledDc == IntPtr.Zero || scaledBitmap == IntPtr.Zero)
            {
                return null;
            }
            var prevDst = SelectObject(scaledDc, scaledBitmap);
            try
            {
                SetStretchBltMode(scaledDc, HALFTONE);
                SetBrushOrgEx(scaledDc, 0, 0, IntPtr.Zero);
                StretchBlt(scaledDc, 0, 0, dstW, dstH, srcDc, 0, 0, srcW, srcH, SRCCOPY);
            }
            finally
            {
                SelectObject(scaledDc, prevDst);
            }

            return BitmapToBgra(scaledBitmap, dstW, dstH);
        }
        finally
        {
            if (srcBitmap != IntPtr.Zero) DeleteObject(srcBitmap);
            if (srcDc != IntPtr.Zero) DeleteDC(srcDc);
            if (scaledBitmap != IntPtr.Zero) DeleteObject(scaledBitmap);
            if (scaledDc != IntPtr.Zero) DeleteDC(scaledDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static CaptureTargetImage? CaptureFromScreen(int originX, int originY, int srcW, int srcH, int maxWidth, int maxHeight)
    {
        var (dstW, dstH) = FitInside(srcW, srcH, maxWidth, maxHeight);
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        IntPtr scaledDc = IntPtr.Zero;
        IntPtr scaledBitmap = IntPtr.Zero;
        try
        {
            scaledDc = CreateCompatibleDC(screenDc);
            scaledBitmap = CreateCompatibleBitmap(screenDc, dstW, dstH);
            if (scaledDc == IntPtr.Zero || scaledBitmap == IntPtr.Zero)
            {
                return null;
            }
            var prev = SelectObject(scaledDc, scaledBitmap);
            try
            {
                SetStretchBltMode(scaledDc, HALFTONE);
                SetBrushOrgEx(scaledDc, 0, 0, IntPtr.Zero);
                StretchBlt(scaledDc, 0, 0, dstW, dstH, screenDc, originX, originY, srcW, srcH, SRCCOPY);
            }
            finally
            {
                SelectObject(scaledDc, prev);
            }
            return BitmapToBgra(scaledBitmap, dstW, dstH);
        }
        finally
        {
            if (scaledBitmap != IntPtr.Zero) DeleteObject(scaledBitmap);
            if (scaledDc != IntPtr.Zero) DeleteDC(scaledDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static CaptureTargetImage? BitmapToBgra(IntPtr hbitmap, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            unsafe
            {
                fixed (byte* p = pixels)
                {
                    var lines = GetDIBits(screenDc, hbitmap, 0, (uint)height, (IntPtr)p, ref bmi, DIB_RGB_COLORS);
                    if (lines == 0)
                    {
                        return null;
                    }
                }
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        // GDI's 32-bit BGRA output from GetDIBits is really BGRx —
        // the alpha byte is undefined / usually zero. WPF's
        // PixelFormats.Bgra32 treats that as "fully transparent", so
        // the thumbnail renders invisible. Force alpha to 0xFF on
        // every pixel so the Image control actually paints.
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
        }
        return new CaptureTargetImage(pixels, width, height, stride);
    }

    private static (int w, int h) FitInside(int srcW, int srcH, int maxW, int maxH)
    {
        var rw = (double)maxW / srcW;
        var rh = (double)maxH / srcH;
        var r = Math.Min(rw, rh);
        if (r >= 1.0)
        {
            return (srcW, srcH);
        }
        return (Math.Max(1, (int)(srcW * r)), Math.Max(1, (int)(srcH * r)));
    }

    // ---------------- Win32 bindings ----------------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;
    private const uint DWMWA_CLOAKED = 14;
    private const uint MONITORINFOF_PRIMARY = 0x1;
    private const int PW_RENDERFULLCONTENT = 0x00000002;
    private const uint SRCCOPY = 0x00CC0020;
    private const int HALFTONE = 4;
    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    private static int GetWindowLong(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr(hWnd, nIndex).ToInt32() : GetWindowLong32(hWnd, nIndex);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageIcon(IntPtr hWnd, int Msg, int wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, ref BITMAP pv);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)]
        public byte[] bmiColors;
    }
}
