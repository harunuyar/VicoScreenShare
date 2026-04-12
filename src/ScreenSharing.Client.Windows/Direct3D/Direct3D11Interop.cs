using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace ScreenSharing.Client.Windows.Direct3D;

/// <summary>
/// P/Invoke + COM glue for crossing between the plain-Win32 D3D11 world (Vortice)
/// and the WinRT <see cref="IDirect3DDevice"/> world that
/// <c>Windows.Graphics.Capture</c> expects. Based on the pattern in
/// robmikh/Win32CaptureSample.
/// </summary>
internal static class Direct3D11Interop
{
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>
    /// Wraps a native D3D11 IDXGIDevice pointer in a WinRT <see cref="IDirect3DDevice"/>.
    /// The returned object is safe to hand to <c>Direct3D11CaptureFramePool.Create</c>.
    /// </summary>
    public static IDirect3DDevice CreateDirect3DDeviceFromDxgi(IntPtr dxgiDevice)
    {
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
        try
        {
            var managed = Marshal.GetObjectForIUnknown(inspectable);
            return (IDirect3DDevice)managed;
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>
    /// Pull the underlying D3D11 texture out of a WinRT <see cref="IDirect3DSurface"/>.
    /// Graphics.Capture frames arrive as <c>IDirect3DSurface</c>; we need the raw
    /// texture to copy it into a staging buffer for CPU readback.
    /// The returned pointer has a ref-count that the caller must release.
    /// </summary>
    public static IntPtr GetDxgiInterfaceFromSurface(IDirect3DSurface surface, Guid iid)
    {
        var access = (IDirect3DDxgiInterfaceAccess)(object)surface;
        var guid = iid;
        access.GetInterface(ref guid, out var result);
        return result;
    }

    public static IntPtr GetD3D11Texture2DFromSurface(IDirect3DSurface surface) =>
        GetDxgiInterfaceFromSurface(surface, IID_ID3D11Texture2D);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        void GetInterface([In] ref Guid iid, out IntPtr ppv);
    }
}
