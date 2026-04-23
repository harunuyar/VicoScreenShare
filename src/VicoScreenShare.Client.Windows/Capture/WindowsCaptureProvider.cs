namespace VicoScreenShare.Client.Windows.Capture;

using System;
using System.Threading.Tasks;
using global::Windows.Graphics.Capture;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Windows.Direct3D;
using CaptureTarget = VicoScreenShare.Client.Platform.CaptureTarget;
using CaptureTargetKind = VicoScreenShare.Client.Platform.CaptureTargetKind;

/// <summary>
/// <see cref="ICaptureProvider"/> backed by <c>Windows.Graphics.Capture</c>. Shows
/// the system picker parented to the main Avalonia window HWND (supplied by the
/// caller as a <see cref="Func{IntPtr}"/> so this class stays unaware of the UI).
/// </summary>
public sealed class WindowsCaptureProvider : ICaptureProvider, IDisposable
{
    private readonly Func<IntPtr> _hwndProvider;
    private readonly D3D11DeviceManager _devices;
    private readonly bool _ownsDevices;
    private bool _disposed;

    public WindowsCaptureProvider(Func<IntPtr> hwndProvider)
    {
        _hwndProvider = hwndProvider;
        _devices = new D3D11DeviceManager();
        _ownsDevices = true;
    }

    /// <summary>
    /// Constructor used when the D3D11 device is created externally so both
    /// the capture source and the encoder can share it. The caller retains
    /// ownership — Dispose leaves the device alone in this mode.
    /// </summary>
    public WindowsCaptureProvider(Func<IntPtr> hwndProvider, D3D11DeviceManager sharedDevices)
    {
        _hwndProvider = hwndProvider;
        _devices = sharedDevices;
        _ownsDevices = false;
    }

    /// <summary>
    /// The underlying device manager, exposed so the encoder factory built
    /// in startup wiring can reuse the same device for zero-copy texture
    /// handoff. Null until <see cref="PickSourceAsync"/> has been called at
    /// least once (which triggers <see cref="D3D11DeviceManager.Initialize"/>).
    /// </summary>
    public D3D11DeviceManager Devices => _devices;

    public async Task<ICaptureSource?> PickSourceAsync(int targetFrameRate)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsCaptureProvider));
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "Windows.Graphics.Capture is not available on this build of Windows.");
        }

        _devices.Initialize();

        var picker = new GraphicsCapturePicker();
        CapturePickerInterop.InitializeWithWindow(picker, _hwndProvider());

        var item = await picker.PickSingleItemAsync();
        if (item is null)
        {
            return null;
        }

        return new WindowsCaptureSource(item, _devices, targetFrameRate);
    }

    public Task<ICaptureSource?> CreateSourceForTargetAsync(CaptureTarget target, int targetFrameRate, int maxPreviewDimension = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind switch
        {
            CaptureTargetKind.Window => CreateSourceForWindowAsync(target.Handle, targetFrameRate, maxPreviewDimension),
            CaptureTargetKind.Monitor => CreateSourceForMonitorAsync(target.Handle, targetFrameRate, maxPreviewDimension),
            _ => Task.FromResult<ICaptureSource?>(null),
        };
    }

    /// <summary>
    /// Build a capture source for a specific <c>HWND</c>, bypassing the
    /// system picker. Used by the custom share picker which already
    /// knows the target. Returns null if the window disappears between
    /// enumeration and materialization.
    /// </summary>
    public async Task<ICaptureSource?> CreateSourceForWindowAsync(IntPtr hwnd, int targetFrameRate, int maxPreviewDimension = 0)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsCaptureProvider));
        }
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "Windows.Graphics.Capture is not available on this build of Windows.");
        }

        _devices.Initialize();
        var item = await GraphicsCaptureItemInterop.CreateForWindowAsync(hwnd).ConfigureAwait(true);
        return item is null ? null : new WindowsCaptureSource(item, _devices, targetFrameRate, maxPreviewDimension);
    }

    /// <summary>
    /// Build a capture source for a specific <c>HMONITOR</c>, bypassing
    /// the system picker. Returns null if the monitor handle has gone
    /// stale (display disconnected).
    /// </summary>
    public async Task<ICaptureSource?> CreateSourceForMonitorAsync(IntPtr hMonitor, int targetFrameRate, int maxPreviewDimension = 0)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsCaptureProvider));
        }
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "Windows.Graphics.Capture is not available on this build of Windows.");
        }

        _devices.Initialize();
        var item = await GraphicsCaptureItemInterop.CreateForMonitorAsync(hMonitor).ConfigureAwait(true);
        return item is null ? null : new WindowsCaptureSource(item, _devices, targetFrameRate, maxPreviewDimension);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsDevices)
        {
            _devices.Dispose();
        }
    }
}
