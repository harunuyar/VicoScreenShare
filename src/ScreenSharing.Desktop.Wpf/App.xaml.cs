using System;
using System.Windows;
using ScreenSharing.Client;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Windows.Capture;
using ScreenSharing.Client.Windows.Direct3D;
using ScreenSharing.Client.Windows.Media.Codecs;

namespace ScreenSharing.Desktop.Wpf;

public partial class App : Application
{
    /// <summary>
    /// Shared D3D11 device — one per process. The WGC framepool, the D3D11
    /// Video Processor scaler, the Media Foundation H.264 encoder/decoder
    /// and the D3DImage video renderer all attach to this device so the
    /// GPU texture pipeline never crosses device boundaries.
    /// </summary>
    public static D3D11DeviceManager? SharedDevices { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DebugLog.Reset();
        DebugLog.Write($"== ScreenSharing (WPF) start @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==");

        var sharedDevices = new D3D11DeviceManager();
        sharedDevices.Initialize();
        SharedDevices = sharedDevices;

        ClientHost.CaptureProviderFactory = hwndProvider => new WindowsCaptureProvider(hwndProvider, sharedDevices);
        ClientHost.VideoCodecCatalog = new VideoCodecCatalog();

        MediaFoundationRuntime.EnsureInitialized();
        if (MediaFoundationRuntime.IsAvailable)
        {
            ClientHost.VideoCodecCatalog.Register(
                new MediaFoundationH264EncoderFactory(sharedDevices.Device),
                new MediaFoundationH264DecoderFactory(sharedDevices.Device));
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SharedDevices?.Dispose();
        base.OnExit(e);
    }
}
