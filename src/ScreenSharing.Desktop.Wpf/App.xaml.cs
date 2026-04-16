using System;
using System.Windows;
using ScreenSharing.Client;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Windows.Capture;
using ScreenSharing.Client.Windows.Direct3D;
using ScreenSharing.Client.Windows.Media.Codecs;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ScreenSharing.Desktop.App;

public partial class App : Application
{
    /// <summary>
    /// Shared D3D11 device — one per process. The WGC framepool, the D3D11
    /// Video Processor scaler, the Media Foundation H.264 encoder/decoder
    /// and the D3DImage video renderer all attach to this device so the
    /// GPU texture pipeline never crosses device boundaries.
    /// </summary>
    public static D3D11DeviceManager? SharedDevices { get; private set; }

    /// <summary>
    /// Receive-side playout queue depth, in frames. Read by
    /// <see cref="Rendering.D3DImageVideoRenderer"/> when it constructs
    /// its <see cref="Rendering.TimestampedFrameQueue"/>. Set from
    /// <c>VideoSettings.ReceiveBufferFrames</c> at startup; updates
    /// require a renderer re-mount (next room join) to take effect.
    /// </summary>
    public static int ReceiveBufferFrames { get; set; } = 5;

    protected override void OnStartup(StartupEventArgs e)
    {
        DebugLog.Reset();
        DebugLog.Write($"== ScreenSharing (WPF) start @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==");

        // Apply Fluent dark theme before any window materializes so the
        // first paint is already themed — otherwise you get a single
        // frame of default-WPF chrome at startup.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: true);

        var sharedDevices = new D3D11DeviceManager();
        sharedDevices.Initialize();
        SharedDevices = sharedDevices;

        ClientHost.CaptureProviderFactory = hwndProvider => new WindowsCaptureProvider(hwndProvider, sharedDevices);
        ClientHost.VideoCodecCatalog = new VideoCodecCatalog();

        // Prime the receiver's prebuffer depth from saved settings so
        // the first renderer instance constructed picks up the user's
        // configured value. The settings store is read for real by the
        // VMs later — this is just a one-shot read for the static.
        try
        {
            var s = new ScreenSharing.Client.Services.SettingsStore().LoadOrCreate();
            ReceiveBufferFrames = Math.Clamp(s.Video.ReceiveBufferFrames, 1, 240);
        }
        catch { /* fall back to default */ }

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
