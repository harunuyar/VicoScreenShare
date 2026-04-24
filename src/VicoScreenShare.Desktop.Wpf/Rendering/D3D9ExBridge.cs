namespace VicoScreenShare.Desktop.App.Rendering;

using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D9;

/// <summary>
/// Owns the D3D9Ex device that bridges shared D3D11 textures into
/// <see cref="System.Windows.Interop.D3DImage"/>. WPF's D3DImage only
/// accepts <c>IDirect3DSurface9</c>, so any D3D11 content we want to
/// composite into the WPF visual tree has to be opened on this device
/// via the legacy <c>D3D11_RESOURCE_MISC_SHARED</c> handle.
///
/// One instance per renderer. The D3D9Ex device has a tiny 1x1 swap
/// chain (we never call Present) and uses FpuPreserve +
/// Multithreaded so the decoder thread and WPF UI thread can both
/// touch resources owned by the paired D3D11 device without racing.
/// </summary>
internal sealed class D3D9ExBridge : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    private readonly IDirect3D9Ex _d3d9;
    private readonly IDirect3DDevice9Ex _device;
    private bool _disposed;

    public D3D9ExBridge()
    {
        _d3d9 = D3D9.Direct3DCreate9Ex();

        var pp = new PresentParameters
        {
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = Format.Unknown,
            BackBufferCount = 1,
            SwapEffect = SwapEffect.Discard,
            DeviceWindowHandle = GetDesktopWindow(),
            Windowed = true,
            PresentationInterval = PresentInterval.Default,
        };

        // HardwareVertexProcessing: standard for any GPU-backed device.
        // Multithreaded: the D3D11 decoder thread writes the shared
        //   texture; the WPF render thread samples it. Both ultimately
        //   touch this device through D3DImage's internals.
        // FpuPreserve: D3D9 defaults to flipping the CPU FPU to
        //   single-precision, which corrupts any code on this thread
        //   that expects IEEE doubles (MF encode thread, Stopwatch
        //   math). Preserve.
        _device = _d3d9.CreateDeviceEx(
            adapter: 0,
            DeviceType.Hardware,
            GetDesktopWindow(),
            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
            pp);
    }

    /// <summary>
    /// Open a D3D11 shared handle as a D3D9 render-target texture.
    /// The returned texture and its level-0 surface alias the same
    /// video memory as the D3D11 texture — writes to the D3D11 side
    /// show up on the D3D9 side after a <c>Flush</c> of the D3D11
    /// context, with no GPU copy.
    /// </summary>
    /// <remarks>
    /// Matching formats: D3D11's <c>B8G8R8A8_UNORM</c> and D3D9's
    /// <c>A8R8G8B8</c> have identical in-memory byte order; any other
    /// pair (R8G8B8A8 on the D3D11 side, X8R8G8B8 on the D3D9 side)
    /// will either fail to open or corrupt the color channels.
    /// The shared-handle value is passed by ref: D3D9 treats it as
    /// both input (the handle to open) and output (usually
    /// overwritten with the same value). Callers hold the IntPtr
    /// obtained from D3D11 <c>IDXGIResource.GetSharedHandle()</c>.
    /// </remarks>
    public IDirect3DTexture9 OpenSharedTexture(int width, int height, IntPtr sharedHandle)
    {
        if (sharedHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Shared handle is null", nameof(sharedHandle));
        }

        var handle = sharedHandle;
        var texture = _device.CreateTexture(
            (uint)width,
            (uint)height,
            1,
            Usage.RenderTarget,
            Format.A8R8G8B8,
            Pool.Default,
            ref handle);
        return texture;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { _device.Dispose(); } catch { }
        try { _d3d9.Dispose(); } catch { }
    }
}
