namespace VicoScreenShare.Desktop.App.Rendering;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Windows.Direct3D;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// Paints decoded BGRA video into a WPF <see cref="D3DImage"/> via a
/// D3D11 → D3D9Ex shared-handle bridge so the video composes into the
/// WPF visual tree like any other image (chrome, overlays, scroll
/// clipping, opacity fades, Z-order all work natively).
///
/// Receiver path: decoder thread pushes onto a
/// <see cref="TimestampedFrameQueue"/> (sized by
/// <c>App.ReceiveBufferFrames</c>); a dedicated
/// <see cref="PaintLoop"/> thread dequeues at content-timestamp
/// cadence, writes into a single shared-handle slot
/// (<see cref="SharedTextureSlot"/>), flushes the D3D11 context, and
/// marshals <c>AddDirtyRect</c> to the UI thread via the dispatcher.
///
/// Self-preview path: no queue, no paint loop — paints inline on the
/// capture-source thread.
///
/// The slot is sized to the source content, not the display
/// rectangle. Display resizes do not touch the GPU slot;
/// <see cref="Image"/> with <see cref="Stretch.Uniform"/> scales the
/// slot to fit the renderer's layout box, preserving aspect ratio
/// with a letterbox that shows the parent's background through. The
/// slot is recreated only when the source resolution changes.
///
/// <see cref="D3DImage.SetBackBuffer"/> is called once per slot
/// instance; every subsequent expose is just <see cref="D3DImage.AddDirtyRect"/>.
///
/// Without a keyed mutex the D3D11 write / D3D9 read ordering relies
/// on <c>context.Flush()</c> submitting before <c>AddDirtyRect</c>
/// signals WPF. On a same-adapter device this is safe in practice —
/// the driver's command queue orders cross-device access to the
/// shared surface.
/// </summary>
public sealed class D3DImageVideoRenderer : FrameworkElement
{
    public D3DImageVideoRenderer()
    {
        _d3dImage = new D3DImage();
        _d3dImage.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
        _imageChild = new Image
        {
            Source = _d3dImage,
            // Uniform preserves aspect ratio; parent background shows
            // through the letterbox. The slot is at source-content
            // size, WPF scales it to the renderer's layout box.
            Stretch = Stretch.Uniform,
        };
        // Video content benefits from linear filtering on upscale;
        // nearest would alias on non-integer scale factors.
        RenderOptions.SetBitmapScalingMode(_imageChild, BitmapScalingMode.Linear);
        AddVisualChild(_imageChild);
        AddLogicalChild(_imageChild);

        _playoutQueue = new TimestampedFrameQueue(App.ReceiveBufferFrames);
        _paintLoop = new PaintLoop(_playoutQueue, PaintFromQueue);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _pulseTimer = new System.Threading.Timer(
            _ => EmitPulse(), null,
            System.TimeSpan.FromSeconds(2),
            System.TimeSpan.FromSeconds(2));
    }

    private void EmitPulse()
    {
        long input, scaled, exposed;
        int playoutDepth, ringBusy, ringSize;
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }
            input = InputFrameCount;
            scaled = ScaledFrameCount;
            exposed = ExposedFrameCount;
            playoutDepth = _playoutQueue.Count;
            ringSize = _textureRing?.Length ?? 0;
            ringBusy = 0;
            if (_ringSlotInUse is not null)
            {
                for (var i = 0; i < _ringSlotInUse.Length; i++)
                {
                    if (_ringSlotInUse[i])
                    {
                        ringBusy++;
                    }
                }
            }
        }
        var dIn = input - _pulseLastInput; _pulseLastInput = input;
        var dSc = scaled - _pulseLastScaled; _pulseLastScaled = scaled;
        var dEx = exposed - _pulseLastExposed; _pulseLastExposed = exposed;
        var wpfTicks = WpfCompositionMetrics.TickCount;
        var dWpf = wpfTicks - _pulseLastWpfTicks; _pulseLastWpfTicks = wpfTicks;
        if (dIn == 0 && dSc == 0 && dEx == 0)
        {
            return;
        }
        // wpfTicks = shared process-wide count of WPF composition ticks
        // across the 2 s window — this is WPF's actual compose rate,
        // NOT our AddDirtyRect submission rate. When wpfTicks << exposed,
        // WPF is coalescing our writes into fewer composites than we
        // submit (i.e. the display is running below dispatch rate,
        // typically monitor-refresh-bound or worse).
        var worstIterMs = _paintLoop.ReadAndResetWorstIterMs();
        // worstIterMs = max painter-iter time observed inside this
        // 2 s window. iterMs spans dequeue-wait + paint cost, so a
        // multi-100 ms value pinpoints exactly when the painter
        // missed cadence even if cumulative input/exposed counts
        // averaged out healthy across the window.
        DebugLog.Write(
            $"[paint-pulse inst={_instanceId}] 2s: input=+{dIn} scaled=+{dSc} exposed=+{dEx} wpfTicks=+{dWpf} " +
            $"paintQ={playoutDepth}/{_playoutQueue.MaxCapacity} ring={ringBusy}/{ringSize} worstIter={worstIterMs:F0}ms");
    }

    public static readonly DependencyProperty ReceiverProperty =
        DependencyProperty.Register(
            nameof(Receiver),
            typeof(ICaptureSource),
            typeof(D3DImageVideoRenderer),
            new PropertyMetadata(null, OnReceiverChanged));

    public ICaptureSource? Receiver
    {
        get => (ICaptureSource?)GetValue(ReceiverProperty);
        set => SetValue(ReceiverProperty, value);
    }

    public static readonly DependencyProperty LocalPreviewSourceProperty =
        DependencyProperty.Register(
            nameof(LocalPreviewSource),
            typeof(ICaptureSource),
            typeof(D3DImageVideoRenderer),
            new PropertyMetadata(null, OnLocalPreviewSourceChanged));

    public ICaptureSource? LocalPreviewSource
    {
        get => (ICaptureSource?)GetValue(LocalPreviewSourceProperty);
        set => SetValue(LocalPreviewSourceProperty, value);
    }

    /// <summary>
    /// Kept for XAML compatibility. The paint thread paces from content
    /// timestamps, not a target fps — this value is currently unused at
    /// runtime but preserved for bindings that predate the paint-loop
    /// pacing rewrite.
    /// </summary>
    public static readonly DependencyProperty NominalFrameRateProperty =
        DependencyProperty.Register(
            nameof(NominalFrameRate),
            typeof(int),
            typeof(D3DImageVideoRenderer),
            new PropertyMetadata(60));

    public int NominalFrameRate
    {
        get => (int)GetValue(NominalFrameRateProperty);
        set => SetValue(NominalFrameRateProperty, value);
    }

    /// <summary>
    /// Radius (in DIPs) of rounded corners. Applied via
    /// <see cref="UIElement.Clip"/> so fades and opacity animations
    /// compose correctly.
    /// </summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(double),
            typeof(D3DImageVideoRenderer),
            new PropertyMetadata(0.0, OnCornerRadiusChanged));

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is D3DImageVideoRenderer self)
        {
            self.UpdateClipGeometry();
        }
    }

    private void UpdateClipGeometry()
    {
        var w = Math.Max(0.0, ActualWidth);
        var h = Math.Max(0.0, ActualHeight);
        if (w <= 0 || h <= 0)
        {
            Clip = null;
            return;
        }
        var radius = Math.Max(0.0, CornerRadius);
        Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0
        ? _imageChild
        : throw new ArgumentOutOfRangeException(nameof(index));

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        _imageChild.Measure(availableSize);
        // Claim the full layout box. Default Image measurement returns
        // min(content, available) which would shrink the renderer to the
        // source's pixel dimensions. We want to occupy the host cell
        // and let the Image's Stretch.Uniform scale within it.
        var width = double.IsInfinity(availableSize.Width) ? _imageChild.DesiredSize.Width : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? _imageChild.DesiredSize.Height : availableSize.Height;
        return new System.Windows.Size(width, height);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        _imageChild.Arrange(new Rect(new Point(0, 0), finalSize));
        UpdateClipGeometry();
        return finalSize;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // Display resizes only update the clip — the GPU slot is
        // sized to the source content, so WPF's Stretch.Uniform
        // handles display-side scaling without any GPU work.
        UpdateClipGeometry();
    }

    // Monotonic renderer ID used by the diagnostic pulse log so
    // multiple live instances (self-preview + N publisher tiles) are
    // distinguishable in the log stream without relying on
    // GetHashCode, which can collide and isn't stable.
    private static int s_nextInstanceId;
    private readonly int _instanceId = System.Threading.Interlocked.Increment(ref s_nextInstanceId);

    private readonly Image _imageChild;
    private readonly D3DImage _d3dImage;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private D3D9ExBridge? _bridge;

    // Single shared-handle slot. Sized to source content. SetBackBuffer
    // is called exactly once per slot instance (on the first expose
    // after creation / resource-change recreate); subsequent exposes
    // only AddDirtyRect. Slot is recreated when the source's
    // width/height changes — that's rare (stream restart, renegotiate),
    // not every layout tick.
    private SharedTextureSlot? _slot;
    private int _slotWidth;
    private int _slotHeight;
    private bool _slotAttached;

    // Stale-pixel probe. Samples 1×1 BGRA from the slot's center pixel
    // immediately after AddDirtyRect, before Unlock. If the same pixel
    // value repeats for >StalePixelLogMs while exposed counter is still
    // ticking, we know WPF is being asked to composite identical pixels
    // many times per second — pipeline beyond the decoder is moving but
    // the actual frame content isn't changing. Distinguishes "pixels
    // frozen on screen" from "WPF composite stalled."
    //
    // Single 1×1 staging texture, allocated lazily, reused for every
    // probe call. The CopySubresourceRegion + Map + Unmap path takes
    // ~100µs on the same device, well within frame budget.
    private ID3D11Texture2D? _stalePixelStaging;
    private uint _lastSampledPixel;
    private long _identicalPixelStreakStartTicks;
    private long _lastStaleLogTicks;
    private long _stalePixelLogCount;
    private const int StalePixelLogMs = 250;
    private const int StalePixelRelogIntervalMs = 1000;
    private bool _hasPendingFrame;
    private bool _exposeQueued;

    // GPU texture ring — hand-off buffer for the GPU decode path.
    // OnTextureArrived GPU-copies the decoder's texture into one of
    // these slots and enqueues the slot index onto the playout queue;
    // PaintFromQueue dequeues and CopyResources from the ring slot
    // into the display slot on the paint thread, then releases the
    // ring slot back to the pool. This is what makes
    // ReceiveBufferFrames work on the GPU path.
    private ID3D11Texture2D?[]? _textureRing;
    private int[]? _ringSlotWidth;
    private int[]? _ringSlotHeight;
    private bool[]? _ringSlotInUse;

    private ICaptureSource? _attachedReceiver;
    private ICaptureSource? _attachedLocalSource;
    private readonly object _renderLock = new();
    private bool _disposed;

    // Receive-side jitter buffer + paint pacer.
    private readonly TimestampedFrameQueue _playoutQueue;
    private readonly PaintLoop _paintLoop;

    private System.Threading.Timer? _pulseTimer;
    private long _pulseLastInput;
    private long _pulseLastScaled;
    private long _pulseLastExposed;
    private long _pulseLastWpfTicks;
    private bool _wpfMetricsSubscribed;
    private long _frameFailureCount;

    /// <summary>
    /// Rate-limited exception log for the hot paint paths. Writes the
    /// first five failures, then every 60th — avoids spamming the log
    /// when every frame trips the same bug while still keeping long
    /// sessions observable.
    /// </summary>
    private void LogFrameFailure(string context, Exception ex)
    {
        var count = System.Threading.Interlocked.Increment(ref _frameFailureCount);
        if (count <= 5 || count % 60 == 0)
        {
            // Include the stack trace on the first 3 occurrences so we can
            // diagnose new failure modes; later events stay terse to avoid
            // flooding the log.
            if (count <= 3)
            {
                DebugLog.Write(
                    $"[renderer] {context} threw (#{count}): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            else
            {
                DebugLog.Write(
                    $"[renderer] {context} threw (#{count}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Every frame that reached <see cref="OnFrameArrived"/>
    /// or <see cref="OnTextureArrived"/> from the decoder.</summary>
    public long InputFrameCount { get; private set; }

    /// <summary>Frames the paint thread wrote into the display slot.</summary>
    public long ScaledFrameCount { get; private set; }

    /// <summary>
    /// <c>Lock + AddDirtyRect + Unlock</c> calls on the UI thread —
    /// frames submitted to WPF for composition. WPF may internally
    /// coalesce several of these into one composite when the paint
    /// rate exceeds the compositor's rate; there is no public API
    /// that reports the actually-composited count.
    /// </summary>
    public long ExposedFrameCount { get; private set; }

    /// <summary>
    /// Legacy alias for <see cref="ExposedFrameCount"/>. Kept for
    /// diagnostic consumers that predate the property rename.
    /// </summary>
    public long PaintedFrameCount => ExposedFrameCount;

    /// <summary>
    /// Frames that arrived from the decoder but weren't submitted to
    /// WPF — jitter-buffer evictions plus any dispatcher coalescing.
    /// </summary>
    public long DroppedFrameCount => InputFrameCount - ExposedFrameCount;

    public double LastPaintMs { get; private set; }

    public (long Input, long Painted, double LastPaintMs) Snapshot()
    {
        lock (_renderLock)
        {
            return (InputFrameCount, ExposedFrameCount, LastPaintMs);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _paintLoop.Start();
        RefreshActiveFrameSource();
        if (!_wpfMetricsSubscribed)
        {
            WpfCompositionMetrics.Subscribe();
            _wpfMetricsSubscribed = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_wpfMetricsSubscribed)
        {
            WpfCompositionMetrics.Unsubscribe();
            _wpfMetricsSubscribed = false;
        }
        TeardownD3D();
    }

    private void TeardownD3D()
    {
        // Stop the paint thread BEFORE tearing D3D state. Dispose joins
        // (500 ms timeout).
        _paintLoop.Dispose();
        try { _pulseTimer?.Dispose(); } catch (Exception) { }
        _pulseTimer = null;
        _playoutQueue.Clear();

        lock (_renderLock)
        {
            _disposed = true;
            DetachReceiver();
            DetachLocalSource();

            // Best-effort detach — if the D3DImage is already in a bad
            // state, we're still winding down and there's nothing useful
            // to do with the exception.
            try
            {
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            }
            catch (Exception) { }
            finally
            {
                try { _d3dImage.Unlock(); } catch (Exception) { }
            }

            _slotAttached = false;
            _hasPendingFrame = false;
            _exposeQueued = false;

            _slot?.Dispose(); _slot = null;
            _slotWidth = 0;
            _slotHeight = 0;
            _stalePixelStaging?.Dispose(); _stalePixelStaging = null;
            _identicalPixelStreakStartTicks = 0;
            _lastStaleLogTicks = 0;
            _lastSampledPixel = 0;
            if (_textureRing is not null)
            {
                for (var i = 0; i < _textureRing.Length; i++)
                {
                    _textureRing[i]?.Dispose();
                    _textureRing[i] = null;
                }
                _textureRing = null;
                _ringSlotWidth = null;
                _ringSlotHeight = null;
                _ringSlotInUse = null;
            }
            _bridge?.Dispose(); _bridge = null;

            _context = null;
            _device = null;
        }
    }

    private bool InitD3DLocked()
    {
        if (_bridge is not null && _device is not null)
        {
            return true;
        }

        var sharedDevices = App.SharedDevices;
        if (sharedDevices is null)
        {
            DebugLog.Write(
                "[renderer] App.SharedDevices is null — cannot init D3D");
            return false;
        }
        _device = sharedDevices.Device;
        _context = sharedDevices.Context;

        try
        {
            _bridge ??= new D3D9ExBridge();
            DebugLog.Write("[renderer] D3D9Ex bridge created");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write(
                $"[renderer] D3D9Ex bridge create threw: {ex.Message}");
            _device = null;
            _context = null;
            return false;
        }
    }

    private static void OnReceiverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is D3DImageVideoRenderer self)
        {
            self.RefreshActiveFrameSource();
        }
    }

    private static void OnLocalPreviewSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is D3DImageVideoRenderer self)
        {
            self.RefreshActiveFrameSource();
        }
    }

    private void RefreshActiveFrameSource()
    {
        DetachReceiver();
        DetachLocalSource();
        if (!IsLoaded)
        {
            return;
        }
        if (LocalPreviewSource is { } local)
        {
            AttachLocalSource(local);
            return;
        }
        if (Receiver is { } receiver)
        {
            AttachReceiver(receiver);
        }
    }

    private void AttachReceiver(ICaptureSource receiver)
    {
        _attachedReceiver = receiver;
        receiver.FrameArrived += OnFrameArrived;
        receiver.TextureArrived += OnTextureArrived;
        // Arm the App's UI-stall probe once a real video receiver
        // is attached. Skips the startup XAML / theme stalls that
        // dominate the log before any video flows.
        VicoScreenShare.Desktop.App.App.VideoRenderActive = true;
        // Wire the session's shared MediaClock through to the paint
        // loop so A/V sync anchors at the actual first-paint moment.
        // Self-preview ICaptureSources don't carry one — that's fine,
        // the paint loop just doesn't publish anchors and audio uses
        // its pre-MediaClock fallback.
        if (receiver is StreamReceiver streamReceiver)
        {
            _paintLoop.SetMediaClock(streamReceiver.MediaClock);
        }
        DebugLog.Write(
            $"[renderer inst={_instanceId}] subscribed to FrameArrived + TextureArrived (receiver={receiver.GetType().Name} hash={receiver.GetHashCode():X8})");
    }

    private void DetachReceiver()
    {
        if (_attachedReceiver is null)
        {
            return;
        }

        try { _attachedReceiver.FrameArrived -= OnFrameArrived; } catch (Exception) { }
        try { _attachedReceiver.TextureArrived -= OnTextureArrived; } catch (Exception) { }
        _attachedReceiver = null;
        _paintLoop.SetMediaClock(null);
        _playoutQueue.Clear();
        DebugLog.Write($"[renderer inst={_instanceId}] unsubscribed FrameArrived + TextureArrived");
    }

    private void AttachLocalSource(ICaptureSource source)
    {
        _attachedLocalSource = source;
        source.FrameArrived += OnFrameArrived;
        source.TextureArrived += OnTextureArrived;
        DebugLog.Write(
            "[renderer] subscribed to ICaptureSource.FrameArrived + TextureArrived (local preview)");
    }

    private void DetachLocalSource()
    {
        if (_attachedLocalSource is null)
        {
            return;
        }

        try { _attachedLocalSource.FrameArrived -= OnFrameArrived; } catch (Exception) { }
        try { _attachedLocalSource.TextureArrived -= OnTextureArrived; } catch (Exception) { }
        _attachedLocalSource = null;
        DebugLog.Write(
            "[renderer] unsubscribed ICaptureSource.FrameArrived + TextureArrived (local preview)");
    }

    private long _onFrameFireCount;

    private void OnFrameArrived(in CaptureFrameData frame)
    {
        var n = System.Threading.Interlocked.Increment(ref _onFrameFireCount);
        if (n == 1)
        {
            DebugLog.Write($"[renderer-probe inst={_instanceId}] OnFrameArrived#1 {frame.Width}x{frame.Height} fmt={frame.Format} attachedReceiver={_attachedReceiver is not null} disposed={_disposed}");
        }
        if (frame.Format != CaptureFramePixelFormat.Bgra8)
        {
            return;
        }

        try
        {
            lock (_renderLock)
            {
                if (_disposed)
                {
                    return;
                }
                InputFrameCount++;
            }

            if (_attachedReceiver is not null)
            {
                // Receiver path: copy bytes into a DecodedVideoFrame,
                // push onto the playout queue. PaintLoop pops on the
                // content-timestamp schedule and calls PaintFromQueue.
                var bgraSize = frame.Height * frame.StrideBytes;
                var bytes = new byte[bgraSize];
                frame.Pixels.Slice(0, bgraSize).CopyTo(bytes);
                var decoded = new DecodedVideoFrame(bytes, frame.Width, frame.Height, frame.Timestamp);
                _playoutQueue.Push(in decoded);
            }
            else
            {
                // Self-preview: paint inline on the capture thread.
                OnFrameArrivedCore(in frame);
            }
        }
        catch (Exception ex)
        {
            LogFrameFailure("OnFrameArrived", ex);
        }
    }

    private long _onTexFireCount;
    private long _onTexLastTicks;
    private long _onTexSpikeLogCount;
    // Self-calibrating spike threshold: the first N gaps are accumulated
    // into _onTexCalibSumMs / _onTexCalibCount; once we have N samples
    // we freeze _onTexSpikeThresholdMs at 3× the average. The 3× factor
    // means "spike = gap longer than 3 frame periods at the source's
    // cadence" = "we missed at least 2 frames in a row" — a consistent
    // semantic across 15 fps preview, 60 fps stream, 120 fps stream.
    // 2× was too tight at high fps (flagged normal single-frame jitter
    // as a spike); 3× filters that out while still catching real stalls.
    // No floor — adding one was the same magic-number mistake as
    // hardcoding 30 ms here in the first place: it penalizes high-fps
    // sources with a tighter threshold than their cadence justifies.
    private const int OnTexCalibSampleCount = 15;
    private const double OnTexSpikeMultiplier = 3.0;
    private double _onTexCalibSumMs;
    private int _onTexCalibCount;
    private double _onTexSpikeThresholdMs;

    private long _onTexTotalSpikeCount;
    private long _ringFullCount;
    private long _onTexLockWaitSpikeCount;

    private void OnTextureArrived(IntPtr nativeTexture, int width, int height, TimeSpan timestamp)
    {
        var n = System.Threading.Interlocked.Increment(ref _onTexFireCount);
        var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var onTexEntryTicks = nowTicks;
        if (n == 1)
        {
            DebugLog.Write($"[renderer-probe inst={_instanceId}] OnTextureArrived#1 nativeTexture={(nativeTexture == IntPtr.Zero ? "0" : "ptr")} {width}x{height} attachedReceiver={_attachedReceiver is not null} disposed={_disposed}");
            _onTexLastTicks = nowTicks;
        }
        else
        {
            var sinceMs = (nowTicks - _onTexLastTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _onTexLastTicks = nowTicks;
            if (_onTexCalibCount < OnTexCalibSampleCount)
            {
                // Calibration phase — accumulate but never log. The
                // first N gaps are noisy (first paint, decoder warmup)
                // but their average still anchors the source's
                // expected cadence well enough for the 2× threshold.
                _onTexCalibSumMs += sinceMs;
                _onTexCalibCount++;
                if (_onTexCalibCount == OnTexCalibSampleCount)
                {
                    var avgMs = _onTexCalibSumMs / OnTexCalibSampleCount;
                    _onTexSpikeThresholdMs = OnTexSpikeMultiplier * avgMs;
                    DebugLog.Write($"[ontex-gap inst={_instanceId}] calibrated: avgGap={avgMs:F1}ms spikeThreshold={_onTexSpikeThresholdMs:F1}ms ({OnTexSpikeMultiplier}× over first {OnTexCalibSampleCount} frames)");
                }
            }
            else if (sinceMs > _onTexSpikeThresholdMs)
            {
                var s = ++_onTexSpikeLogCount;
                if (s <= 200 || s % 50 == 0)
                {
                    DebugLog.Write($"[ontex-gap inst={_instanceId}] gap={sinceMs:F1}ms frame#{n} {width}x{height} (>{_onTexSpikeThresholdMs:F1}ms)");
                }
            }
        }
        if (nativeTexture == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        // Self-balanced refcount: the decoder does NOT pre-AddRef the
        // texture before fanning out via the receiver's TextureArrived
        // event. Wrapping with `new ID3D11Texture2D(ptr)` does not
        // AddRef on construction but DOES Release on dispose — so an
        // explicit AddRef here pairs with the using-var Release for a
        // net zero refcount delta from this consumer, regardless of
        // how many other TextureArrived subscribers exist or whether
        // they touch refcount. Wrap at the TOP of the function so
        // every early return path (ring full, _disposed, init failure,
        // null-guard, self-preview) disposes the wrapper and balances
        // the AddRef.
        using var sourceTexture = new ID3D11Texture2D(nativeTexture);
        sourceTexture.AddRef();

        int slot;
        try
        {
            // Layer 7b probe: same _renderLock from the OnTextureArrived
            // direction. Catches contention where the decoder thread
            // is the one waiting (e.g., painter is in PaintFromQueue
            // doing CopyResource).
            var lockT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_renderLock)
            {
                var lockMs = (System.Diagnostics.Stopwatch.GetTimestamp() - lockT0) * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency;
                if (lockMs > 5.0)
                {
                    var c = System.Threading.Interlocked.Increment(ref _onTexLockWaitSpikeCount);
                    if (c <= 200 || c % 50 == 0)
                    {
                        DebugLog.Write($"[recv-l7b-ontex-lock inst={_instanceId}] decoder thread waited {lockMs:F1}ms for _renderLock");
                    }
                }
                if (_disposed)
                {
                    return;
                }
                if (!InitD3DLocked())
                {
                    return;
                }

                InputFrameCount++;

                // Self-preview fast path: no queue, paint inline.
                if (_attachedReceiver is null)
                {
                    EnsureSlotLocked(width, height);
                    if (_slot is null)
                    {
                        return;
                    }
                    _context!.CopyResource(_slot.D3D11Texture, sourceTexture);
                    _context.Flush();
                    _hasPendingFrame = true;
                    ScaledFrameCount++;
                    ScheduleExposeLocked();
                    return;
                }

                // Receiver path: GPU-copy into an owned ring slot, then
                // push onto the playout queue.
                slot = AcquireRingSlotLocked();
                if (slot < 0)
                {
                    // Layer 5 probe: ring slot exhausted. Means the
                    // painter hasn't released slots fast enough — a
                    // direct symptom of the painter falling behind
                    // before any visible queue starvation.
                    var c = System.Threading.Interlocked.Increment(ref _ringFullCount);
                    if (c <= 200 || c % 50 == 0)
                    {
                        DebugLog.Write($"[recv-l5-ringfull inst={_instanceId}] AcquireRingSlot returned -1 (painter behind); frame dropped");
                    }
                    return;
                }

                EnsureRingSlotTextureLocked(slot, width, height);

                // Pinpoint which null caused the NRE we keep seeing here.
                // Each branch reports the SPECIFIC missing piece so the next
                // log shows exactly which invariant broke instead of the
                // bare "NullReferenceException" we have today.
                if (_context is null)
                {
                    DebugLog.Write($"[renderer-bug] OnTextureArrived: _context is null after InitD3DLocked (slot={slot} {width}x{height})");
                    return;
                }
                if (_textureRing is null)
                {
                    DebugLog.Write($"[renderer-bug] OnTextureArrived: _textureRing is null after AcquireRingSlotLocked (slot={slot})");
                    return;
                }
                if (_textureRing[slot] is null)
                {
                    DebugLog.Write($"[renderer-bug] OnTextureArrived: _textureRing[{slot}] is null after EnsureRingSlotTextureLocked ({width}x{height})");
                    return;
                }

                _context.CopyResource(_textureRing[slot]!, sourceTexture);
            }
        }
        catch (Exception ex)
        {
            LogFrameFailure("OnTextureArrived", ex);
            return;
        }

        // Push OUTSIDE the render lock (OnDropped re-enters).
        var capturedSlot = slot;
        var queuedFrame = new DecodedVideoFrame(
            Bgra: Array.Empty<byte>(),
            Width: width,
            Height: height,
            Timestamp: timestamp,
            TextureSlot: capturedSlot,
            OnDropped: () => ReleaseRingSlot(capturedSlot));

        // Layer 6 probe: TimestampedFrameQueue.Push time. Push takes
        // an internal lock and may fire the OnDropped of an overflow
        // victim (which re-enters this renderer's ReleaseRingSlot).
        // > 2 ms means meaningful contention, e.g., the painter is
        // holding the queue lock during dequeue while we try to push.
        var pushT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _playoutQueue.Push(in queuedFrame);
        var pushMs = (System.Diagnostics.Stopwatch.GetTimestamp() - pushT0) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        if (pushMs > 2.0)
        {
            DebugLog.Write($"[recv-l6-push inst={_instanceId}] Push={pushMs:F1}ms qDepthAfter={_playoutQueue.Count}");
        }

        // Layer 5b: total OnTextureArrived time. Includes lock
        // acquire, ring slot acquire, EnsureRingSlotTextureLocked,
        // CopyResource, queue Push. Ideally <2 ms; >5 ms means lock
        // contention OR EnsureRingSlotTextureLocked is rebuilding a
        // texture (cold path).
        var onTexMs = (System.Diagnostics.Stopwatch.GetTimestamp() - onTexEntryTicks) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        if (onTexMs > 5.0)
        {
            var c = System.Threading.Interlocked.Increment(ref _onTexTotalSpikeCount);
            if (c <= 200 || c % 50 == 0)
            {
                DebugLog.Write($"[recv-l5-ontex inst={_instanceId}] total={onTexMs:F1}ms slot={capturedSlot} {width}x{height}");
            }
        }
    }

    /// <summary>
    /// PaintLoop callback. Runs on the paint thread after the loop has
    /// slept to the next frame's due time.
    /// </summary>
    private void PaintFromQueue(in DecodedVideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        if (frame.TextureSlot >= 0)
        {
            var slot = frame.TextureSlot;
            try
            {
                // Layer 7 probe: _renderLock acquire wait. The painter
                // thread shares this lock with the decoder's
                // OnTextureArrived path. If OnTextureArrived holds the
                // lock during a slow CopyResource, the painter waits
                // here. > 5 ms wait = real cross-thread contention.
                var lockT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_renderLock)
                {
                    var lockMs = (System.Diagnostics.Stopwatch.GetTimestamp() - lockT0) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
                    if (lockMs > 5.0)
                    {
                        DebugLog.Write($"[recv-l7-renderlock inst={_instanceId}] paint thread waited {lockMs:F1}ms for _renderLock (decoder thread holding?)");
                    }
                    if (_disposed || _textureRing is null || _textureRing[slot] is null)
                    {
                        return;
                    }
                    if (!InitD3DLocked())
                    {
                        return;
                    }
                    EnsureSlotLocked(frame.Width, frame.Height);
                    if (_slot is null)
                    {
                        return;
                    }
                    if (_context is null)
                    {
                        DebugLog.Write($"[renderer-bug] PaintFromQueue: _context is null (slot={slot} {frame.Width}x{frame.Height})");
                        return;
                    }
                    var ringTex = _textureRing[slot];
                    if (ringTex is null)
                    {
                        DebugLog.Write($"[renderer-bug] PaintFromQueue: _textureRing[{slot}] became null between guard and use");
                        return;
                    }
                    if (_slot.D3D11Texture is null)
                    {
                        DebugLog.Write($"[renderer-bug] PaintFromQueue: _slot.D3D11Texture is null after EnsureSlotLocked ({frame.Width}x{frame.Height})");
                        return;
                    }
                    // Split timing: CopyResource queues a GPU command
                    // (typically <0.1 ms) but blocks at submit if the
                    // destination is still being read by a prior GPU op
                    // (e.g., WPF compositor presenting the previous
                    // frame from this ring slot, or another thread
                    // holding the immediate context). Flush submits
                    // queued commands and blocks if the GPU command
                    // queue is back-pressured. Splitting tells us
                    // which: a 60 ms copyResource means a wait-on-prior;
                    // a 60 ms flush means GPU overloaded.
                    var copyT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _context.CopyResource(_slot.D3D11Texture, ringTex);
                    var copyMidTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    _context.Flush();
                    var copyEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    var copyResMs = (copyMidTicks - copyT0) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
                    var flushMs = (copyEndTicks - copyMidTicks) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
                    var copyMs = copyResMs + flushMs;
                    if (copyMs > 5.0)
                    {
                        var gen0 = GC.CollectionCount(0);
                        var gen1 = GC.CollectionCount(1);
                        var gen2 = GC.CollectionCount(2);
                        DebugLog.Write($"[gpu-copy-spike inst={_instanceId}] copyResource={copyResMs:F1}ms flush={flushMs:F1}ms slot={slot} size={frame.Width}x{frame.Height} gen0={gen0} gen1={gen1} gen2={gen2}");
                    }
                    _hasPendingFrame = true;
                    ScaledFrameCount++;
                    ScheduleExposeLocked();
                }
            }
            catch (Exception ex)
            {
                LogFrameFailure("PaintFromQueue (gpu)", ex);
            }
            finally
            {
                ReleaseRingSlot(slot);
            }
            return;
        }

        if (frame.Bgra is null || frame.Bgra.Length == 0)
        {
            return;
        }

        // CPU path.
        var strideBytes = frame.Width * 4;
        var data = new CaptureFrameData(
            frame.Bgra.AsSpan(0, frame.Height * strideBytes),
            frame.Width,
            frame.Height,
            strideBytes,
            CaptureFramePixelFormat.Bgra8,
            frame.Timestamp);
        try
        {
            OnFrameArrivedCore(in data);
        }
        catch (Exception ex)
        {
            LogFrameFailure("PaintFromQueue (cpu)", ex);
        }
    }

    /// <summary>
    /// Shared CPU-upload + paint sequence. Uploads BGRA bytes directly
    /// into the shared slot's D3D11 texture — no intermediate upload
    /// texture, since the slot is at source-content size.
    /// </summary>
    private void OnFrameArrivedCore(in CaptureFrameData frame)
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }
            if (!InitD3DLocked())
            {
                return;
            }

            var width = frame.Width;
            var height = frame.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            EnsureSlotLocked(width, height);
            if (_slot is null)
            {
                return;
            }

            unsafe
            {
                fixed (byte* src = frame.Pixels)
                {
                    _context!.UpdateSubresource(
                        _slot.D3D11Texture,
                        0,
                        null,
                        (IntPtr)src,
                        (uint)frame.StrideBytes,
                        (uint)(frame.StrideBytes * height));
                }
            }
            _context!.Flush();

            _hasPendingFrame = true;
            ScaledFrameCount++;
            ScheduleExposeLocked();
        }
    }

    /// <summary>
    /// Ensure the display slot matches the source-content dimensions.
    /// A dimension change disposes the old slot and creates a new one;
    /// the next expose rebinds <see cref="D3DImage"/> via
    /// <c>SetBackBuffer</c>. Dimension changes are rare (stream restart
    /// or resolution renegotiation) — NOT every display-size change.
    /// </summary>
    private void EnsureSlotLocked(int srcW, int srcH)
    {
        if (_slot is not null && _slotWidth == srcW && _slotHeight == srcH)
        {
            return;
        }

        _slot?.Dispose();
        _slot = null;
        _slotAttached = false;
        _slotWidth = srcW;
        _slotHeight = srcH;

        try
        {
            _slot = new SharedTextureSlot(_device!, _bridge!, srcW, srcH);
        }
        catch (Exception ex)
        {
            DebugLog.Write(
                $"[renderer] slot create threw: {ex.Message}");
            _slot = null;
        }
    }

    /// <summary>
    /// Marshal the expose onto the UI thread. Coalesced via
    /// <see cref="_exposeQueued"/>: if an expose is already pending,
    /// just flag the latest write as "up next" and return; the running
    /// dispatcher call picks up whichever frame is freshest when it
    /// actually runs. Called under <see cref="_renderLock"/>.
    /// </summary>
    private long _scheduleExposeTicks;

    private void ScheduleExposeLocked()
    {
        if (_exposeQueued)
        {
            return;
        }
        _exposeQueued = true;
        // Capture the moment we asked the UI thread to paint. The actual
        // ExposePendingFrame run on the dispatcher logs the queue→run
        // latency; a high value means UI-thread contention.
        _scheduleExposeTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        // Render priority is WPF's "the visual tree has changed,
        // paint on the next tick" tier — the right level for a
        // video frame exposure. Send (which preempts Input and
        // Render) would starve layout and input handling.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(ExposePendingFrame));
    }

    private long _expoLastTicks;
    private long _expoLogCount;
    private int _expoLastGcGen0;
    private int _expoLastGcGen1;
    private int _expoLastGcGen2;

    private void ExposePendingFrame()
    {
        var enterTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var queueLatencyMs = (enterTicks - _scheduleExposeTicks) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        var sinceLastExpoMs = _expoLastTicks == 0
            ? 0.0
            : (enterTicks - _expoLastTicks) * 1000.0
              / System.Diagnostics.Stopwatch.Frequency;
        _expoLastTicks = enterTicks;
        // Capture GC counts at expose entry. Diff against the previous
        // expose tells us if a Gen0/1/2 collection happened during the
        // gap. A Gen2 collection on the UI thread can pause for tens
        // to hundreds of ms — visible as a queueLatency spike with
        // gen2Delta>0. Background GC modes only stop-the-world for
        // pin/compact phases, but those are still the relevant cause
        // when the spike correlates.
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var gen0Delta = gen0 - _expoLastGcGen0;
        var gen1Delta = gen1 - _expoLastGcGen1;
        var gen2Delta = gen2 - _expoLastGcGen2;
        _expoLastGcGen0 = gen0;
        _expoLastGcGen1 = gen1;
        _expoLastGcGen2 = gen2;

        lock (_renderLock)
        {
            _exposeQueued = false;
            if (_disposed || !_hasPendingFrame || _slot is null)
            {
                return;
            }
            if (!_d3dImage.IsFrontBufferAvailable)
            {
                _hasPendingFrame = false;
                return;
            }
            _hasPendingFrame = false;

            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long lockMs = 0, addRectMs = 0, unlockMs = 0;
            try
            {
                // Layer 8 probe: split D3DImage timings. Lock blocks
                // when WPF's render thread is currently presenting
                // from the back buffer; AddDirtyRect schedules the
                // composite; Unlock releases the surface. Each step
                // can independently spike under GPU contention.
                var lt0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _d3dImage.Lock();
                lockMs = System.Diagnostics.Stopwatch.GetTimestamp() - lt0;
                if (!_slotAttached)
                {
                    _d3dImage.SetBackBuffer(
                        D3DResourceType.IDirect3DSurface9,
                        _slot.D3D9Surface.NativePointer);
                    _slotAttached = true;
                }
                var rt0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _slot.Width, _slot.Height));
                addRectMs = System.Diagnostics.Stopwatch.GetTimestamp() - rt0;
                ExposedFrameCount++;
                ProbeStalePixel();
            }
            catch (Exception ex)
            {
                DebugLog.Write(
                    $"[renderer] D3DImage expose threw: {ex.Message}");
                _slotAttached = false;
            }
            finally
            {
                var ut0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { _d3dImage.Unlock(); } catch (Exception) { }
                unlockMs = System.Diagnostics.Stopwatch.GetTimestamp() - ut0;
            }
            LastPaintMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
            var freq = (double)System.Diagnostics.Stopwatch.Frequency;
            var lockMsF = lockMs * 1000.0 / freq;
            var addRectMsF = addRectMs * 1000.0 / freq;
            var unlockMsF = unlockMs * 1000.0 / freq;
            if (lockMsF > 3.0 || addRectMsF > 3.0 || unlockMsF > 3.0)
            {
                DebugLog.Write($"[recv-l8-d3dimg inst={_instanceId}] Lock={lockMsF:F1}ms AddRect={addRectMsF:F1}ms Unlock={unlockMsF:F1}ms");
            }

            // Hot-path diagnostic. UI-thread Render priority should run
            // ScheduleExposeLocked → ExposePendingFrame in <16 ms. If
            // queueLatency > 30 ms, the UI thread is blocked. If
            // sinceLastExpo > 30 ms, the visible refresh skipped a beat —
            // exactly what the user perceives as stutter.
            if (queueLatencyMs > 30.0 || sinceLastExpoMs > 30.0 || LastPaintMs > 10.0)
            {
                var n = ++_expoLogCount;
                if (n <= 200 || n % 50 == 0)
                {
                    DebugLog.Write(
                        $"[expose-spike inst={_instanceId}] queueLatency={queueLatencyMs:F1}ms sinceLastExpo={sinceLastExpoMs:F1}ms paint={LastPaintMs:F1}ms gcGen0={gen0Delta} gcGen1={gen1Delta} gcGen2={gen2Delta}");
                }
            }
        }
    }

    /// <summary>
    /// 1×1 BGRA readback of the slot's center pixel right after
    /// AddDirtyRect. If the value repeats for &gt;<see cref="StalePixelLogMs"/>
    /// while ExposedFrameCount keeps ticking, we're posting identical
    /// pixels to WPF many times per second — visible as a "frozen"
    /// image even though the paint loop reports healthy. Logs the
    /// duration of each detected stale window so we can tell whether
    /// what the user sees as a multi-second freeze is actually that
    /// (vs WPF compose stalls or pipeline downstream of WPF).
    /// </summary>
    private void ProbeStalePixel()
    {
        if (_slot is null || _context is null) { return; }
        try
        {
            if (_stalePixelStaging is null)
            {
                _stalePixelStaging = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = 1,
                    Height = 1,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                });
            }
            var cx = _slot.Width / 2;
            var cy = _slot.Height / 2;
            _context.CopySubresourceRegion(
                _stalePixelStaging, 0, 0, 0, 0,
                _slot.D3D11Texture, 0,
                new Vortice.Mathematics.Box(cx, cy, 0, cx + 1, cy + 1, 1));
            var mapped = _context.Map(_stalePixelStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            uint pixel;
            unsafe
            {
                pixel = *(uint*)mapped.DataPointer.ToPointer();
            }
            _context.Unmap(_stalePixelStaging, 0);

            var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            var freq = (double)System.Diagnostics.Stopwatch.Frequency;
            if (pixel == _lastSampledPixel)
            {
                if (_identicalPixelStreakStartTicks == 0)
                {
                    _identicalPixelStreakStartTicks = nowTicks;
                }
                else
                {
                    var streakMs = (nowTicks - _identicalPixelStreakStartTicks) * 1000.0 / freq;
                    var sinceLastLogMs = (nowTicks - _lastStaleLogTicks) * 1000.0 / freq;
                    if (streakMs >= StalePixelLogMs
                        && (_lastStaleLogTicks == 0 || sinceLastLogMs >= StalePixelRelogIntervalMs))
                    {
                        var n = ++_stalePixelLogCount;
                        DebugLog.Write(
                            $"[paint-stale inst={_instanceId}] center pixel 0x{pixel:X8} unchanged for {streakMs:F0}ms (streak #{n})");
                        _lastStaleLogTicks = nowTicks;
                        // Trigger upstream PLI: the decoder is producing
                        // identical pixels — reference state is poisoned
                        // and no error surfaced, so PLI is the only way
                        // out. StreamReceiver's debounce caps this at
                        // one PLI per 500ms regardless of how often the
                        // probe fires, so the relog interval (1s) is
                        // strictly slower than the debounce.
                        if (Receiver is VicoScreenShare.Client.Media.StreamReceiver sr)
                        {
                            sr.RequestKeyframeFromRenderer($"paint-stale {streakMs:F0}ms");
                        }
                    }
                }
            }
            else
            {
                if (_identicalPixelStreakStartTicks != 0 && _lastStaleLogTicks != 0)
                {
                    // Fired at end of a logged streak so we can see how
                    // long the freeze actually lasted.
                    var totalMs = (nowTicks - _identicalPixelStreakStartTicks) * 1000.0 / freq;
                    if (totalMs >= StalePixelLogMs)
                    {
                        DebugLog.Write(
                            $"[paint-stale-end inst={_instanceId}] pixel changed to 0x{pixel:X8}, streak lasted {totalMs:F0}ms");
                    }
                }
                _identicalPixelStreakStartTicks = 0;
                _lastStaleLogTicks = 0;
                _lastSampledPixel = pixel;
            }
        }
        catch
        {
            // Diagnostic must not crash the paint path.
        }
    }

    // --- Ring slot management ---

    private int AcquireRingSlotLocked()
    {
        if (_textureRing is null)
        {
            var size = _playoutQueue.MaxCapacity;
            _textureRing = new ID3D11Texture2D?[size];
            _ringSlotWidth = new int[size];
            _ringSlotHeight = new int[size];
            _ringSlotInUse = new bool[size];
        }

        for (var i = 0; i < _textureRing.Length; i++)
        {
            if (!_ringSlotInUse![i])
            {
                _ringSlotInUse[i] = true;
                return i;
            }
        }

        // Every slot is in flight. Force the queue to evict its oldest
        // entry; its OnDropped hook releases a slot back to us.
        //
        // IMPORTANT: we must drop _renderLock while SkipToLatest runs,
        // because its OnDropped callback re-enters this renderer via
        // ReleaseRingSlot which takes _renderLock. Without the drop
        // we self-deadlock. The callers of this method hold the lock
        // at depth exactly 1 (guaranteed because the only call sites
        // are the two branches of OnTextureArrived, neither of which
        // is re-entrant), so Exit + Enter restores the same state.
        // If the call graph ever changes to allow nested acquisitions,
        // this trick breaks.
        System.Threading.Monitor.Exit(_renderLock);
        try
        {
            _playoutQueue.SkipToLatest(_playoutQueue.MaxCapacity - 1);
        }
        finally
        {
            System.Threading.Monitor.Enter(_renderLock);
        }

        for (var i = 0; i < _textureRing.Length; i++)
        {
            if (!_ringSlotInUse![i])
            {
                _ringSlotInUse[i] = true;
                return i;
            }
        }
        return -1;
    }

    private void ReleaseRingSlot(int slot)
    {
        if (slot < 0)
        {
            return;
        }
        lock (_renderLock)
        {
            if (_ringSlotInUse is null || slot >= _ringSlotInUse.Length)
            {
                return;
            }
            _ringSlotInUse[slot] = false;
        }
    }

    private void EnsureRingSlotTextureLocked(int slot, int width, int height)
    {
        if (_textureRing is null || _ringSlotWidth is null || _ringSlotHeight is null)
        {
            return;
        }

        if (_textureRing[slot] is not null
            && _ringSlotWidth[slot] == width
            && _ringSlotHeight[slot] == height)
        {
            return;
        }

        _textureRing[slot]?.Dispose();
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _textureRing[slot] = _device!.CreateTexture2D(desc);
        _ringSlotWidth[slot] = width;
        _ringSlotHeight[slot] = height;
    }

    /// <summary>
    /// When the OS suspends GPU composition (lock screen, remote
    /// session, DWM restart) the D3DImage drops its front buffer. On
    /// the recovery edge we force a reattach of the slot.
    /// </summary>
    private void OnFrontBufferAvailableChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_d3dImage.IsFrontBufferAvailable)
        {
            return;
        }

        lock (_renderLock)
        {
            if (_disposed || _slot is null)
            {
                return;
            }
            try
            {
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(
                    D3DResourceType.IDirect3DSurface9,
                    _slot.D3D9Surface.NativePointer);
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _slot.Width, _slot.Height));
                _slotAttached = true;
            }
            catch (Exception ex)
            {
                DebugLog.Write(
                    $"[renderer] front-buffer reattach threw: {ex.Message}");
                _slotAttached = false;
            }
            finally
            {
                try { _d3dImage.Unlock(); } catch (Exception) { }
            }
        }
    }
}
