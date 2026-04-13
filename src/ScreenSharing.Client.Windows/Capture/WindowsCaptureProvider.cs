using System;
using System.Threading.Tasks;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Windows.Direct3D;
using Windows.Graphics.Capture;

namespace ScreenSharing.Client.Windows.Capture;

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

    public async Task<ICaptureSource?> PickSourceAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsCaptureProvider));

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "Windows.Graphics.Capture is not available on this build of Windows.");
        }

        _devices.Initialize();

        var picker = new GraphicsCapturePicker();
        CapturePickerInterop.InitializeWithWindow(picker, _hwndProvider());

        var item = await picker.PickSingleItemAsync();
        if (item is null) return null;

        return new WindowsCaptureSource(item, _devices);
    }

    public Task<ICaptureSource?> PickScreenAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsCaptureProvider));

        // DDA wants the device up before IDXGIOutput1.DuplicateOutput is
        // called — Initialize is idempotent so a second call from a later
        // Share click is fine.
        _devices.Initialize();

        // Output 0 = primary monitor. Multi-monitor picker is a follow-up;
        // the vast majority of use cases here are single-monitor gaming.
        ICaptureSource source = new DxgiDesktopDuplicationSource(_devices, 0, "Primary Display");
        return Task.FromResult<ICaptureSource?>(source);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsDevices)
        {
            _devices.Dispose();
        }
    }
}
