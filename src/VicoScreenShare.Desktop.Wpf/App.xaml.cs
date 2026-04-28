namespace VicoScreenShare.Desktop.App;

using System;
using System.Windows;
using VicoScreenShare.Client;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Windows.Audio;
using VicoScreenShare.Client.Windows.Capture;
using VicoScreenShare.Client.Windows.Direct3D;
using VicoScreenShare.Client.Windows.Media;
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
        ClientHost.CaptureTargetEnumerator = new Win32CaptureTargetEnumerator();
        ClientHost.VideoCodecCatalog = new VideoCodecCatalog();

        // Shared-content audio wiring. The capture provider picks the
        // default render endpoint (loopback); each publisher session
        // gets its own resampler (stateless, cheap) and each viewer tile
        // its own renderer (NAudio mixes their outputs at the system
        // layer). Opus encoder / decoder factories are preconstructed
        // in ClientHost — pure Concentus, no per-host configuration.
        ClientHost.AudioCaptureProvider = new WasapiAudioCaptureProvider();
        ClientHost.AudioResamplerFactory = () => new NAudioResampler();
        ClientHost.AudioRendererFactory = () => new WasapiAudioRenderer();

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
            // Both encoder and decoder use the shared D3D11 device so the
            // decoder's BGRA output textures can be handed over to the
            // renderer (which also runs on the shared device) via the
            // GpuOutputHandler path — no PCIe readback, which otherwise
            // caps single-stream 4K decode at ~50 fps.
            //
            // The tradeoff is ID3D11Multithread serializing decoder
            // submissions when multiple StreamReceivers decode in parallel;
            // that cost shows up as ~15% throughput reduction in the
            // 3-streams-on-one-machine test case, but the GPU fast path
            // dominates for realistic setups (one viewer per machine).
            // A proper fix for the multi-viewer case is cross-device
            // shared textures; deferred as a focused follow-up.
            // H264EncoderFactorySelector is the composite that prefers a
            // direct NVENC SDK path on NVIDIA GPUs (when implemented) and
            // falls back to the MFT path otherwise. During Phase 1 it
            // unconditionally returns the MFT encoder; the wiring is here
            // so the capability probe runs at startup and the diagnostic
            // log already shows whether NVENC will be picked once Phase 2
            // lands.
            ClientHost.VideoCodecCatalog.Register(
                new H264EncoderFactorySelector(sharedDevices.Device),
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
