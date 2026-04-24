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
        DebugLog.Write(
            $"[paint-pulse inst={_instanceId}] 2s: input=+{dIn} scaled=+{dSc} exposed=+{dEx} wpfTicks=+{dWpf} " +
            $"paintQ={playoutDepth}/{_playoutQueue.MaxCapacity} ring={ringBusy}/{ringSize}");
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
            DebugLog.Write(
                $"[renderer] {context} threw (#{count}): {ex.GetType().Name}: {ex.Message}");
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
        DebugLog.Write(
            $"[renderer] subscribed to FrameArrived + TextureArrived (receiver={receiver.GetType().Name})");
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
        _playoutQueue.Clear();
        DebugLog.Write("[renderer] unsubscribed FrameArrived + TextureArrived");
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

    private void OnFrameArrived(in CaptureFrameData frame)
    {
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

    private void OnTextureArrived(IntPtr nativeTexture, int width, int height, TimeSpan timestamp)
    {
        if (nativeTexture == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        int slot;
        try
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

                InputFrameCount++;

                // Self-preview fast path: no queue, paint inline.
                if (_attachedReceiver is null)
                {
                    EnsureSlotLocked(width, height);
                    if (_slot is null)
                    {
                        return;
                    }
                    using var previewSource = new ID3D11Texture2D(nativeTexture);
                    _context!.CopyResource(_slot.D3D11Texture, previewSource);
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
                    return;
                }

                EnsureRingSlotTextureLocked(slot, width, height);

                using var sourceTexture = new ID3D11Texture2D(nativeTexture);
                _context!.CopyResource(_textureRing![slot]!, sourceTexture);
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
        _playoutQueue.Push(in queuedFrame);
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
                lock (_renderLock)
                {
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
                    _context!.CopyResource(_slot.D3D11Texture, _textureRing[slot]!);
                    _context.Flush();
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
    private void ScheduleExposeLocked()
    {
        if (_exposeQueued)
        {
            return;
        }
        _exposeQueued = true;
        // Render priority is WPF's "the visual tree has changed,
        // paint on the next tick" tier — the right level for a
        // video frame exposure. Send (which preempts Input and
        // Render) would starve layout and input handling.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(ExposePendingFrame));
    }

    private void ExposePendingFrame()
    {
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
            try
            {
                _d3dImage.Lock();
                if (!_slotAttached)
                {
                    _d3dImage.SetBackBuffer(
                        D3DResourceType.IDirect3DSurface9,
                        _slot.D3D9Surface.NativePointer);
                    _slotAttached = true;
                }
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _slot.Width, _slot.Height));
                ExposedFrameCount++;
            }
            catch (Exception ex)
            {
                DebugLog.Write(
                    $"[renderer] D3DImage expose threw: {ex.Message}");
                _slotAttached = false;
            }
            finally
            {
                try { _d3dImage.Unlock(); } catch (Exception) { }
            }
            LastPaintMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
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
