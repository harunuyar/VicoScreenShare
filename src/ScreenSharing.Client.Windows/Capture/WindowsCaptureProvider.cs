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
    private bool _disposed;

    public WindowsCaptureProvider(Func<IntPtr> hwndProvider)
    {
        _hwndProvider = hwndProvider;
        _devices = new D3D11DeviceManager();
    }

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _devices.Dispose();
    }
}
