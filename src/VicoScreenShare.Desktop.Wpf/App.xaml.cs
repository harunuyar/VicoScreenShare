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
                new H264DecoderFactorySelector(sharedDevices.Device));

            // AV1 is registered alongside H.264. The encoder factory selector
            // prefers the direct NVENC SDK path on RTX 40+ silicon and falls
            // back to a Media Foundation AV1 encoder MFT (Intel Arc / Xe2 /
            // AMD RDNA 3+); user-overridable from Settings via
            // VideoSettings.Av1Backend. AV1 codec visibility is gated on
            // EITHER backend being available — Quick Sync / AMF boxes get
            // AV1 even without NVENC.
            //
            // The decoder factory selector picks NVDEC when available and
            // falls back to the Microsoft "AV1 Video Extension" MFT decoder
            // otherwise — user-overridable via VideoSettings.Av1DecoderBackend.
            // NVDEC handles 4K AV1 IDRs in single-digit ms; MFT typically
            // spends 30-45 ms and produces visible micro-stutters at IDR
            // boundaries.
            var av1EncoderSelector = new Av1EncoderFactorySelector(sharedDevices.Device);
            var av1DecoderSelector = new Av1DecoderFactorySelector(sharedDevices.Device);
            ClientHost.VideoCodecCatalog.Register(
                av1EncoderSelector,
                av1DecoderSelector);
        }

        StartDispatcherStallProbe();
        StartWpfRenderTickProbe();
        base.OnStartup(e);
    }

    /// <summary>
    /// WPF compositor render-thread cadence probe. Subscribes to
    /// <see cref="System.Windows.Media.CompositionTarget.Rendering"/>,
    /// which fires once per WPF render tick on the UI thread but is
    /// driven by WPF's internal render thread. A gap ≥ 50 ms between
    /// successive ticks means WPF's compositor/GPU presentation
    /// stalled — could be a driver hang, GPU pipeline contention,
    /// or an extended <c>D3DImage</c> backbuffer lock. The
    /// <see cref="StartDispatcherStallProbe"/> probe can't see this:
    /// the dispatcher stays responsive even when the render thread
    /// is stuck. Gated on <see cref="VideoRenderActive"/> so startup
    /// noise is filtered.
    /// </summary>
    private void StartWpfRenderTickProbe()
    {
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        long lastTicks = 0;
        var stallLogCount = 0L;
        System.Windows.Media.CompositionTarget.Rendering += (_, _) =>
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lastTicks != 0 && VideoRenderActive)
            {
                var gapMs = (now - lastTicks) * 1000.0 / freq;
                if (gapMs >= 50.0)
                {
                    var gen0 = GC.CollectionCount(0);
                    var gen1 = GC.CollectionCount(1);
                    var gen2 = GC.CollectionCount(2);
                    var n = ++stallLogCount;
                    if (n <= 200 || n % 50 == 0)
                    {
                        DebugLog.Write(
                            $"[wpf-rendertick-stall] gap={gapMs:F1}ms gen0={gen0} gen1={gen1} gen2={gen2}");
                    }
                }
            }
            lastTicks = now;
        };
    }

    /// <summary>
    /// Active-measurement UI-thread block probe. A background-thread
    /// <see cref="System.Threading.Timer"/> fires every 16 ms; each
    /// tick records a wall-time stamp and calls
    /// <c>Dispatcher.InvokeAsync</c> at <c>Send</c> priority (highest;
    /// preempts everything except input). The lambda measures how
    /// long it took to reach the UI thread — that gap IS the UI
    /// block time. <c>DispatcherTimer</c> at Background priority
    /// (used by the previous version) gave too much noise: Background
    /// runs after Render, so its tick latency reflects Render-tier
    /// work too. Send-priority round-trip isolates real UI blocking.
    ///
    /// Logs only when video is actively being rendered
    /// (<see cref="VideoRenderActive"/> set by the renderer when
    /// frames start flowing) so we don't drown in startup XAML /
    /// theme load stalls. Threshold 50 ms — anything under that is
    /// indistinguishable from a normal WPF render-tick hold.
    /// </summary>
    public static volatile bool VideoRenderActive;

    private System.Threading.Timer? _stallProbeTimer;

    private void StartDispatcherStallProbe()
    {
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        var lastGen0 = GC.CollectionCount(0);
        var lastGen1 = GC.CollectionCount(1);
        var lastGen2 = GC.CollectionCount(2);
        var stallLogCount = 0L;
        var inFlight = 0;
        _stallProbeTimer = new System.Threading.Timer(_ =>
        {
            // Skip if a previous tick is still queued — measuring the
            // *next* one when the previous is still pending doesn't
            // tell us anything new.
            if (System.Threading.Interlocked.Exchange(ref inFlight, 1) == 1)
            {
                return;
            }
            if (!VideoRenderActive)
            {
                System.Threading.Interlocked.Exchange(ref inFlight, 0);
                return;
            }
            var sentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            Dispatcher.InvokeAsync(() =>
            {
                var arrivedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                var rtMs = (arrivedTicks - sentTicks) * 1000.0 / freq;
                if (rtMs >= 50.0)
                {
                    var gen0 = GC.CollectionCount(0);
                    var gen1 = GC.CollectionCount(1);
                    var gen2 = GC.CollectionCount(2);
                    var n = ++stallLogCount;
                    if (n <= 200 || n % 50 == 0)
                    {
                        DebugLog.Write(
                            $"[ui-stall] sendRoundTrip={rtMs:F1}ms gcGen0={gen0 - lastGen0} gcGen1={gen1 - lastGen1} gcGen2={gen2 - lastGen2}");
                    }
                    lastGen0 = gen0;
                    lastGen1 = gen1;
                    lastGen2 = gen2;
                }
                System.Threading.Interlocked.Exchange(ref inFlight, 0);
            }, System.Windows.Threading.DispatcherPriority.Send);
        }, null, dueTime: TimeSpan.FromMilliseconds(500), period: TimeSpan.FromMilliseconds(16));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SharedDevices?.Dispose();
        base.OnExit(e);
    }
}
