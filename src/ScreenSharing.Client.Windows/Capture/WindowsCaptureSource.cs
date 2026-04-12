using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Windows.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using global::Windows.Graphics;
using global::Windows.Graphics.Capture;
using global::Windows.Graphics.DirectX;
using global::Windows.Graphics.DirectX.Direct3D11;

namespace ScreenSharing.Client.Windows.Capture;

/// <summary>
/// Windows.Graphics.Capture-backed implementation of <see cref="ICaptureSource"/>.
/// Owns a <see cref="Direct3D11CaptureFramePool"/> and a staging texture; each
/// incoming frame is copied to the staging texture, mapped to CPU, and handed to
/// subscribers as a <see cref="CaptureFrameData"/>. The byte buffer is pooled so
/// there is no per-frame allocation on the hot path.
/// </summary>
public sealed class WindowsCaptureSource : ICaptureSource
{
    private readonly GraphicsCaptureItem _item;
    private readonly D3D11DeviceManager _devices;
    private readonly Stopwatch _timer = Stopwatch.StartNew();

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _stagingTexture;
    private int _stagingWidth;
    private int _stagingHeight;
    private byte[] _cpuBuffer = Array.Empty<byte>();
    private bool _closed;
    private bool _disposed;

    public WindowsCaptureSource(GraphicsCaptureItem item, D3D11DeviceManager devices)
    {
        _item = item;
        _devices = devices;
        DisplayName = item.DisplayName;
        _item.Closed += OnItemClosed;
    }

    public string DisplayName { get; }

    public event FrameArrivedHandler? FrameArrived;

    public event Action? Closed;

    public Task StartAsync()
    {
        if (_session is not null || _closed || _disposed)
        {
            return Task.CompletedTask;
        }

        var size = _item.Size;
        _framePool = Direct3D11CaptureFramePool.Create(
            _devices.WinRTDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            size: size);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = true; } catch { }
        _session.StartCapture();

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _session?.Dispose();
        _session = null;
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
            _framePool = null;
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _item.Closed -= OnItemClosed;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _closed = true;
        Closed?.Invoke();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;

        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        var width = frame.ContentSize.Width;
        var height = frame.ContentSize.Height;
        if (width <= 0 || height <= 0) return;

        if (width != _stagingWidth || height != _stagingHeight)
        {
            RecreateStagingTexture(width, height);
            try
            {
                sender.Recreate(
                    _devices.WinRTDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    new SizeInt32 { Width = width, Height = height });
            }
            catch (ObjectDisposedException) { /* raced with Dispose */ }
            // Skip this frame; the recreate invalidates downstream references.
            return;
        }

        var texPtr = Direct3D11Interop.GetD3D11Texture2DFromSurface(frame.Surface);
        if (texPtr == IntPtr.Zero) return;
        using (var sourceTexture = new ID3D11Texture2D(texPtr))
        {
            _devices.Context.CopyResource(_stagingTexture!, sourceTexture);
        }

        var mapped = _devices.Context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = width * 4;
            var required = height * rowBytes;
            if (_cpuBuffer.Length < required)
            {
                _cpuBuffer = new byte[required];
            }

            unsafe
            {
                var src = (byte*)mapped.DataPointer;
                fixed (byte* dst = _cpuBuffer)
                {
                    for (var y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            src + (long)y * mapped.RowPitch,
                            dst + (long)y * rowBytes,
                            rowBytes,
                            rowBytes);
                    }
                }
            }

            var data = new CaptureFrameData(
                _cpuBuffer.AsSpan(0, required),
                width,
                height,
                rowBytes,
                CaptureFramePixelFormat.Bgra8,
                _timer.Elapsed);
            FrameArrived?.Invoke(in data);
        }
        finally
        {
            _devices.Context.Unmap(_stagingTexture!, 0);
        }
    }

    private void RecreateStagingTexture(int width, int height)
    {
        _stagingTexture?.Dispose();
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _stagingTexture = _devices.Device.CreateTexture2D(desc);
        _stagingWidth = width;
        _stagingHeight = height;
    }
}
