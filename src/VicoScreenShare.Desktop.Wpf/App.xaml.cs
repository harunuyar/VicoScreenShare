namespace VicoScreenShare.Desktop.App;

using System;
using System.Windows;
using VicoScreenShare.Client;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Capture;
using VicoScreenShare.Client.Windows.Direct3D;
using VicoScreenShare.Client.Windows.Media.Codecs;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

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

        // Opt this process out of Windows background-process throttling as
        // early as possible — before any media thread starts. See
        // BackgroundThrottlingOptOut.cs for the rationale. Without this a
        // backgrounded subscriber window loses ~70% of RTP packets because
        // the OS slows the receive thread enough for the kernel UDP queue
        // to overflow.
        BackgroundThrottlingOptOut.Apply();

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
            var s = new VicoScreenShare.Client.Services.SettingsStore().LoadOrCreate();
            ReceiveBufferFrames = Math.Clamp(s.Video.ReceiveBufferFrames, 1, 240);
        }
        catch { /* fall back to default */ }

        MediaFoundationRuntime.EnsureInitialized();
        if (MediaFoundationRuntime.IsAvailable)
        {
            // Encoder stays on the shared device — its input textures come
            // from WGC capture, which owns that device, and the GPU scaler +
            // encoder MFT all need to be on the same device as the source.
            //
            // Decoder uses the no-device factory, so each StreamReceiver's
            // CreateDecoder() allocates a PRIVATE D3D11 device for that
            // decoder instance. When watching multiple streams in parallel
            // this stops ID3D11Multithread from serializing decoder
            // submissions across streams — a 4090 has 5 NVDEC engines and
            // we want each decoder on its own device so it can drive a
            // different engine concurrently. The renderer-side GPU handoff
            // (cross-device texture pointers) is disabled in this mode;
            // the decoder uses the CPU-bytes path, which is fine for
            // downscaled-for-display tile paint.
            ClientHost.VideoCodecCatalog.Register(
                new MediaFoundationH264EncoderFactory(sharedDevices.Device),
                new MediaFoundationH264DecoderFactory());
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SharedDevices?.Dispose();
        base.OnExit(e);
    }
}
