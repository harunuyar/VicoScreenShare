using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenSharing.Client;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Services;
using ScreenSharing.Client.Windows.Capture;
using ScreenSharing.Client.Windows.Media.Codecs;
using ScreenSharing.Desktop.App.Services;
using ScreenSharing.Desktop.App.ViewModels;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;

namespace ScreenSharing.Desktop.App.Views;

/// <summary>
/// Standalone diagnostic page: capture → render, with no SFU, no
/// signaling, no network. Two modes, selected by the "Run through
/// encode/decode" checkbox:
///
///  - Unchecked (texture-direct): binds the picked
///    <see cref="WindowsCaptureSource"/> to the renderer's
///    <c>LocalPreviewSource</c>. GPU-to-GPU handoff, no encoder, no
///    decoder. This is the path production uses for the self-preview
///    tile.
///
///  - Checked (encode/decode): wires the source through the real
///    production <see cref="CaptureStreamer"/> + Media Foundation H.264
///    encoder + decoder via an internal <see cref="EncodeDecodeBridge"/>
///    that implements <see cref="ICaptureSource"/> and feeds decoded
///    BGRA frames into the renderer's <c>Receiver</c> (jitter buffer +
///    paced paints). This is the production pipeline used for remote
///    streams minus only the network hop, so any bug that shows up in
///    real screen sharing will also show up here.
///
/// Settings are re-read from <see cref="SettingsStore"/> on every Pick
/// so the test matches whatever the user has configured in the shell.
/// Back navigation goes through
/// <see cref="CaptureTestViewModel.GoBackCommand"/>.
/// </summary>
public partial class CaptureTestView : UserControl
{
    private WindowsCaptureSource? _source;
    private EncodeDecodeBridge? _bridge;
    private NoCodecCpuReadbackAdapter? _noCodecAdapter;

    private readonly DispatcherTimer _statsTimer;
    private readonly Stopwatch _statsSw = Stopwatch.StartNew();
    private long _statsLastSampleTicks;
    private long _statsLastWgcCount;
    private long _statsLastDispatchedCount;
    private long _statsLastInputCount;
    private long _statsLastPaintedCount;

    public CaptureTestView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;

        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statsTimer.Tick += OnStatsTick;
        _statsTimer.Start();
    }

    private async void PickButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await DetachSourceAsync();

            if (!GraphicsCaptureSession.IsSupported())
            {
                StatusText.Text = "WGC not supported on this build of Windows.";
                return;
            }

            var sharedDevices = App.SharedDevices
                ?? throw new InvalidOperationException("App.SharedDevices is null — startup did not run.");

            var hwnd = new WindowInteropHelper(Window.GetWindow(this)!).EnsureHandle();

            var picker = new GraphicsCapturePicker();
            global::WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var item = await picker.PickSingleItemAsync();
            if (item is null)
            {
                StatusText.Text = "Picker cancelled.";
                return;
            }

            // Pull the real settings — same VideoSettings CaptureStreamer
            // would consume in the room view.
            var videoSettings = new SettingsStore().LoadOrCreate().Video;
            var fps = Math.Clamp(videoSettings.TargetFrameRate, 1, 240);

            _source = new WindowsCaptureSource(item, sharedDevices, fps);

            if (EncodeDecodeBox.IsChecked == true)
            {
                // Encode → decode roundtrip through the real production
                // CaptureStreamer + MF H.264 encoder + decoder. The
                // bridge implements ICaptureSource and fires
                // FrameArrived with decoded BGRA, so the renderer's
                // Receiver path handles it identically to a remote
                // stream coming off the network.
                var catalog = ClientHost.VideoCodecCatalog ?? new VideoCodecCatalog();
                var resolved = catalog.ResolveOrFallback(videoSettings.Codec);
                var encoderFactory = resolved.encoderFactory;
                var decoderFactory = resolved.decoderFactory;
                _bridge = new EncodeDecodeBridge(_source, encoderFactory, decoderFactory, videoSettings);
                await _source.StartAsync();
                _bridge.Start();
                Renderer.NominalFrameRate = fps;
                Renderer.Receiver = _bridge;
                StatusText.Text = $"\"{item.DisplayName}\" — {fps} fps, {videoSettings.TargetBitrate / 1_000_000} Mbps ({resolved.selected} encode/decode)";
                DebugLog.Write($"[capture-test] picked \"{item.DisplayName}\", {resolved.selected} encode/decode, fps={fps}");
            }
            else
            {
                // No-codec mode: the NoCodecCpuReadbackAdapter wraps the
                // source's TextureArrived event, does a CPU readback per
                // admitted frame, and fires FrameArrived as an
                // ICaptureSource. That lets us route the no-codec path
                // through the SAME renderer.Receiver → TimestampedFrameQueue
                // → PresentLoop chain the encode/decode mode uses, so
                // both modes exercise the real production render path.
                _noCodecAdapter = new NoCodecCpuReadbackAdapter(_source);
                await _source.StartAsync();
                Renderer.NominalFrameRate = fps;
                Renderer.Receiver = _noCodecAdapter;
                StatusText.Text = $"\"{item.DisplayName}\" — {fps} fps (no-codec readback)";
                DebugLog.Write($"[capture-test] picked \"{item.DisplayName}\", no-codec readback, fps={fps}");
            }

            StopButton.IsEnabled = true;
            ResetStatsBaseline();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Pick failed: {ex.Message}";
            DebugLog.Write($"[capture-test] PickSourceAsync threw: {ex}");
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await DetachSourceAsync();
        StopButton.IsEnabled = false;
        StatusText.Text = "Stopped.";
    }

    /// <summary>
    /// Settings gear — navigates the main window to the existing
    /// SettingsView via the same code path the home view's gear uses,
    /// and wires the back factory so the user returns to a fresh
    /// capture-test page.
    /// </summary>
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Application.Current.MainWindow;
        if (mainWindow?.DataContext is NavigationService nav &&
            nav.Current is CaptureTestViewModel)
        {
            var settingsStore = new SettingsStore();
            var clientSettings = settingsStore.LoadOrCreate();
            var settingsVm = new SettingsViewModel(
                clientSettings,
                settingsStore,
                nav,
                () => new CaptureTestViewModel(nav, () => CreateHomeViewModel(nav)));
            nav.NavigateTo(settingsVm);
        }
    }

    /// <summary>
    /// Best-effort lookup of an existing HomeViewModel so the settings
    /// back navigation from capture-test lands on a real home. In
    /// normal flow this hits the cached home VM from the shell.
    /// </summary>
    private static ViewModelBase CreateHomeViewModel(INavigationHost nav)
    {
        if (Application.Current.MainWindow?.DataContext is NavigationService current
            && current.Current is HomeViewModel existing)
        {
            return existing;
        }
        throw new InvalidOperationException(
            "Cannot rebuild HomeViewModel from CaptureTestView — opening settings from the test page requires the home VM to still be reachable.");
    }

    private async Task DetachSourceAsync()
    {
        Renderer.LocalPreviewSource = null;
        Renderer.Receiver = null;
        if (_bridge is not null)
        {
            try { await _bridge.DisposeAsync(); }
            catch (Exception ex) { DebugLog.Write($"[capture-test] bridge dispose threw: {ex.Message}"); }
            _bridge = null;
        }
        if (_noCodecAdapter is not null)
        {
            try { _noCodecAdapter.Dispose(); }
            catch (Exception ex) { DebugLog.Write($"[capture-test] no-codec adapter dispose threw: {ex.Message}"); }
            _noCodecAdapter = null;
        }
        if (_source is not null)
        {
            try { await _source.DisposeAsync(); }
            catch (Exception ex) { DebugLog.Write($"[capture-test] source dispose threw: {ex.Message}"); }
            _source = null;
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _statsTimer.Stop();
        await DetachSourceAsync();
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        if (_source is null)
        {
            StatsText.Text = "No source picked.";
            return;
        }

        var nowTicks = _statsSw.ElapsedTicks;
        if (_statsLastSampleTicks == 0)
        {
            _statsLastSampleTicks = nowTicks;
            _statsLastWgcCount = _source.WgcFrameCount;
            _statsLastDispatchedCount = _source.DispatchedFrameCount;
            _statsLastInputCount = Renderer.InputFrameCount;
            _statsLastPaintedCount = Renderer.PaintedFrameCount;
            StatsText.Text = "Sampling...";
            return;
        }

        var elapsedSec = (nowTicks - _statsLastSampleTicks) / (double)Stopwatch.Frequency;
        if (elapsedSec <= 0) return;

        var wgcCount = _source.WgcFrameCount;
        var dispatchedCount = _source.DispatchedFrameCount;
        var inputCount = Renderer.InputFrameCount;
        var paintedCount = Renderer.PaintedFrameCount;
        var lastPaintMs = Renderer.LastPaintMs;

        var wgcFps = (wgcCount - _statsLastWgcCount) / elapsedSec;
        var dispatchedFps = (dispatchedCount - _statsLastDispatchedCount) / elapsedSec;
        var inputFps = (inputCount - _statsLastInputCount) / elapsedSec;
        var paintedFps = (paintedCount - _statsLastPaintedCount) / elapsedSec;

        StatsText.Text =
            $"WGC arrivals : {wgcFps,6:F1} fps  ({wgcCount} total)\n" +
            $"Dispatched   : {dispatchedFps,6:F1} fps  ({dispatchedCount} total)\n" +
            $"Renderer in  : {inputFps,6:F1} fps  ({inputCount} total)\n" +
            $"Painted      : {paintedFps,6:F1} fps  ({paintedCount} total)\n" +
            $"Last paint   : {lastPaintMs:F2} ms";

        _statsLastSampleTicks = nowTicks;
        _statsLastWgcCount = wgcCount;
        _statsLastDispatchedCount = dispatchedCount;
        _statsLastInputCount = inputCount;
        _statsLastPaintedCount = paintedCount;
    }

    private void ResetStatsBaseline()
    {
        _statsLastSampleTicks = 0;
        _statsLastWgcCount = 0;
        _statsLastDispatchedCount = 0;
        _statsLastInputCount = 0;
        _statsLastPaintedCount = 0;
    }

    /// <summary>
    /// Wraps a <see cref="WindowsCaptureSource"/> with the real
    /// production <see cref="CaptureStreamer"/> and an MF H.264 decoder
    /// so the test runs frames through the same encode/decode loop the
    /// production screen share uses, minus the network hop. Implements
    /// <see cref="ICaptureSource"/> so the renderer's
    /// <see cref="D3DImageVideoRenderer.Receiver"/> path can paint the
    /// decoded BGRA bytes — exactly the same path that paints frames
    /// arriving from a remote peer in a real room.
    /// </summary>
    private sealed class EncodeDecodeBridge : ICaptureSource
    {
        private readonly WindowsCaptureSource _capture;
        private readonly IVideoDecoder _decoder;
        private readonly CaptureStreamer _streamer;
        private bool _disposed;

        public EncodeDecodeBridge(
            WindowsCaptureSource capture,
            IVideoEncoderFactory encoderFactory,
            IVideoDecoderFactory decoderFactory,
            VideoSettings settings)
        {
            _capture = capture;
            _decoder = decoderFactory.CreateDecoder();
            _streamer = new CaptureStreamer(capture, OnEncoded, settings, encoderFactory);
        }

        public string DisplayName => _capture.DisplayName;

        public event FrameArrivedHandler? FrameArrived;

        public event TextureArrivedHandler? TextureArrived { add { } remove { } }

        public event Action? Closed
        {
            add => _capture.Closed += value;
            remove => _capture.Closed -= value;
        }

        public void Start() => _streamer.Start();

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync()
        {
            _streamer.Stop();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            try { _streamer.Dispose(); } catch { }
            try { _decoder.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }

        private void OnEncoded(uint durationRtp, byte[] encoded, TimeSpan contentTimestamp)
        {
            if (_disposed || encoded is null || encoded.Length == 0) return;

            IReadOnlyList<DecodedVideoFrame> frames;
            try
            {
                frames = _decoder.Decode(encoded, contentTimestamp);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[capture-test] decoder threw: {ex.Message}");
                return;
            }

            for (var i = 0; i < frames.Count; i++)
            {
                var decoded = frames[i];
                if (decoded.Bgra is null || decoded.Bgra.Length == 0) continue;
                var bgraSize = decoded.Width * decoded.Height * 4;
                if (decoded.Bgra.Length < bgraSize) continue;

                var data = new CaptureFrameData(
                    decoded.Bgra.AsSpan(0, bgraSize),
                    decoded.Width,
                    decoded.Height,
                    decoded.Width * 4,
                    CaptureFramePixelFormat.Bgra8,
                    decoded.Timestamp);
                FrameArrived?.Invoke(in data);
            }
        }
    }

    /// <summary>
    /// Test-only adapter that lets the no-codec capture-test mode feed
    /// the same <see cref="D3DImageVideoRenderer.Receiver"/> path the
    /// encode/decode bridge uses. It subscribes to the source's
    /// <see cref="ICaptureSource.TextureArrived"/>, does a CPU readback
    /// into a packed BGRA buffer, and fires <see cref="ICaptureSource.FrameArrived"/>
    /// so the renderer's queue + PresentLoop receives frames through the
    /// same path a decoded remote stream would. Slightly higher CPU
    /// cost than a true zero-copy texture path, acceptable because this
    /// is a diagnostic harness; production still runs GPU textures all
    /// the way to the encoder.
    /// </summary>
    private sealed class NoCodecCpuReadbackAdapter : ICaptureSource, IDisposable
    {
        private readonly WindowsCaptureSource _capture;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private ID3D11Texture2D? _staging;
        private int _stagingWidth;
        private int _stagingHeight;
        private byte[] _bgraBuffer = Array.Empty<byte>();
        private readonly object _lock = new();
        private bool _disposed;

        public NoCodecCpuReadbackAdapter(WindowsCaptureSource source)
        {
            _capture = source;
            _device = App.SharedDevices!.Device;
            _context = App.SharedDevices!.Context;
            source.TextureArrived += OnTextureArrived;
        }

        public string DisplayName => _capture.DisplayName;
        public event FrameArrivedHandler? FrameArrived;
        public event TextureArrivedHandler? TextureArrived { add { } remove { } }
        public event Action? Closed
        {
            add => _capture.Closed += value;
            remove => _capture.Closed -= value;
        }

        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _capture.TextureArrived -= OnTextureArrived; } catch { }
                _staging?.Dispose();
                _staging = null;
            }
        }

        private void OnTextureArrived(IntPtr nativeTexture, int width, int height, TimeSpan timestamp)
        {
            if (nativeTexture == IntPtr.Zero || width <= 0 || height <= 0) return;

            lock (_lock)
            {
                if (_disposed) return;

                EnsureStagingLocked(width, height);
                if (_staging is null) return;

                using var sourceTexture = new ID3D11Texture2D(nativeTexture);
                _context.CopyResource(_staging, sourceTexture);

                var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var rowBytes = width * 4;
                    var required = height * rowBytes;
                    if (_bgraBuffer.Length < required)
                    {
                        _bgraBuffer = new byte[required];
                    }

                    unsafe
                    {
                        var src = (byte*)mapped.DataPointer;
                        fixed (byte* dst = _bgraBuffer)
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
                        _bgraBuffer.AsSpan(0, required),
                        width,
                        height,
                        rowBytes,
                        CaptureFramePixelFormat.Bgra8,
                        timestamp);
                    FrameArrived?.Invoke(in data);
                }
                finally
                {
                    _context.Unmap(_staging, 0);
                }
            }
        }

        private void EnsureStagingLocked(int width, int height)
        {
            if (_staging is not null && _stagingWidth == width && _stagingHeight == height) return;
            _staging?.Dispose();
            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                ArraySize = 1,
                MipLevels = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };
            _staging = _device.CreateTexture2D(desc);
            _stagingWidth = width;
            _stagingHeight = height;
        }
    }
}
