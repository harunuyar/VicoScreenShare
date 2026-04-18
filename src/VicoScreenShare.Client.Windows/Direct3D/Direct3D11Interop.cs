using System;
using System.Runtime.InteropServices;
using WinRT;
using Windows.Graphics.DirectX.Direct3D11;

namespace VicoScreenShare.Client.Windows.Direct3D;

/// <summary>
/// P/Invoke + CsWinRT glue for crossing between the plain-Win32 D3D11 world
/// (Vortice) and the WinRT <see cref="IDirect3DDevice"/> / <see cref="IDirect3DSurface"/>
/// world that <c>Windows.Graphics.Capture</c> expects.
///
/// Neither direction works with classic COM interop (<c>Marshal.GetObjectForIUnknown</c>
/// returns a <c>System.__ComObject</c> that cannot be cast to a CsWinRT projected
/// interface, and casting a CsWinRT projected type to a hand-rolled
/// <c>[ComImport]</c> interface throws). Both directions have to go through CsWinRT's
/// own marshaling helpers: <see cref="MarshalInspectable{T}.FromAbi"/> to promote a
/// raw <c>IInspectable*</c> to a projected type, and
/// <see cref="CastExtensions.As{T}(object)"/> to QI a projected type for a legacy
/// <c>[ComImport]</c> interface such as <see cref="IDirect3DDxgiInterfaceAccess"/>.
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
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            // MarshalInspectable<T>.FromAbi takes a raw IInspectable* (adds a ref of
            // its own to produce the projected wrapper) so the caller must still
            // release the original ref from CreateDirect3D11DeviceFromDXGIDevice.
            return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != IntPtr.Zero)
            {
                Marshal.Release(inspectable);
            }
        }
    }

    /// <summary>
    /// Pulls the underlying D3D11 texture out of a WinRT <see cref="IDirect3DSurface"/>.
    /// Graphics.Capture frames arrive as <c>IDirect3DSurface</c>; we need the raw
    /// texture to copy it into a staging buffer for CPU readback. The returned
    /// pointer has a ref-count that the caller must release when it's finished
    /// (e.g. by wrapping it in a Vortice <c>ID3D11Texture2D</c>, which owns it).
    /// </summary>
    public static IntPtr GetDxgiInterfaceFromSurface(IDirect3DSurface surface, Guid iid)
    {
        // CastExtensions.As<T> is the CsWinRT QI helper: it takes the projected
        // surface, queries its underlying IUnknown for the legacy IID of
        // IDirect3DDxgiInterfaceAccess, and returns a wrapper that forwards vtable
        // calls through COM. Unlike a plain C# cast this does not go through
        // __ComObject RCW lookup and therefore does not hit the CCW failure.
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var guid = iid;
        Marshal.ThrowExceptionForHR(access.GetInterface(ref guid, out var result));
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
        [PreserveSig]
        int GetInterface([In] ref Guid iid, out IntPtr ppv);
    }
}
