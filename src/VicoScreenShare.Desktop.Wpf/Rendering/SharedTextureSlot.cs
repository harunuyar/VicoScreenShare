namespace VicoScreenShare.Desktop.App.Rendering;

using System;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3D9 = Vortice.Direct3D9;

/// <summary>
/// One slot of the D3DImage display ring. Owns a paired
/// <see cref="ID3D11Texture2D"/> (created with
/// <c>D3D11_RESOURCE_MISC_SHARED</c>) and the matching D3D9 texture
/// opened from the shared handle on <see cref="D3D9ExBridge"/>'s
/// device. The D3D9 level-0 surface is what gets handed to
/// <see cref="System.Windows.Interop.D3DImage.SetBackBuffer(System.Windows.Interop.D3DResourceType, IntPtr)"/>.
/// </summary>
/// <remarks>
/// Two slots alternate each frame so WPF composition can sample slot
/// [i-1] while the decoder thread fills slot [i]. Without the double
/// buffer, the scaler's write and WPF's read race on the same pixels
/// — visible as tearing.
/// </remarks>
internal sealed class SharedTextureSlot : IDisposable
{
    public ID3D11Texture2D D3D11Texture { get; }
    public D3D9.IDirect3DTexture9 D3D9Texture { get; }
    public D3D9.IDirect3DSurface9 D3D9Surface { get; }
    public int Width { get; }
    public int Height { get; }

    private bool _disposed;

    public SharedTextureSlot(ID3D11Device device, D3D9ExBridge bridge, int width, int height)
    {
        Width = width;
        Height = height;

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            ArraySize = 1,
            MipLevels = 1,
            // BGRA maps byte-for-byte onto D3D9's A8R8G8B8 format, which
            // is the only color format the shared-handle path accepts.
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            // RenderTarget is required because the D3D11 VideoProcessor
            // writes into this texture via VideoProcessorBlt.
            // ShaderResource so the scaler's input view still validates
            // (Video Processor rejects textures without SRV binding).
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            // Legacy SHARED, NOT SHARED_NTHANDLE — D3D9 can only open
            // the legacy handle type. KeyedMutex is unavailable for
            // the same reason; sync is handled via context.Flush() on
            // the D3D11 side plus WPF's frame-based composition pull.
            MiscFlags = ResourceOptionFlags.Shared,
        };
        D3D11Texture = device.CreateTexture2D(desc);

        using var dxgiResource = D3D11Texture.QueryInterface<IDXGIResource>();
        var sharedHandle = dxgiResource.SharedHandle;
        D3D9Texture = bridge.OpenSharedTexture(width, height, sharedHandle);
        D3D9Surface = D3D9Texture.GetSurfaceLevel(0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { D3D9Surface.Dispose(); } catch { }
        try { D3D9Texture.Dispose(); } catch { }
        try { D3D11Texture.Dispose(); } catch { }
    }
}
