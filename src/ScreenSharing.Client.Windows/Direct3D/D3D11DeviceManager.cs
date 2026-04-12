using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace ScreenSharing.Client.Windows.Direct3D;

/// <summary>
/// Owns the single D3D11 device that backs the capture pipeline. Exposes both the
/// native Vortice device/context (needed for staging-texture copies) and the WinRT
/// <see cref="IDirect3DDevice"/> wrapper (needed for the Graphics.Capture framepool).
/// </summary>
public sealed class D3D11DeviceManager : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private bool _disposed;

    public ID3D11Device Device => _device ?? throw new InvalidOperationException("D3D11DeviceManager not initialized.");
    public ID3D11DeviceContext Context => _context ?? throw new InvalidOperationException("D3D11DeviceManager not initialized.");
    public IDirect3DDevice WinRTDevice => _winrtDevice ?? throw new InvalidOperationException("D3D11DeviceManager not initialized.");

    public void Initialize()
    {
        if (_device is not null) return;

        var flags = DeviceCreationFlags.BgraSupport;
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };

        var hr = D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            flags,
            featureLevels,
            out _device,
            out _,
            out _context);
        hr.CheckError();

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        _winrtDevice = Direct3D11Interop.CreateDirect3DDeviceFromDxgi(dxgiDevice.NativePointer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // _winrtDevice is a managed WinRT wrapper; GC will release the COM ref.
        _winrtDevice = null;

        _context?.Dispose();
        _device?.Dispose();
        _context = null;
        _device = null;
    }
}
