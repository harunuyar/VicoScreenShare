namespace VicoScreenShare.Desktop.App.Rendering;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Windows.Direct3D;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// Paints decoded BGRA video into a WPF <see cref="D3DImage"/> via the
/// D3D11 → D3D9Ex shared-handle bridge. The element is a regular
/// <see cref="FrameworkElement"/> — chrome, overlays, scroll clipping,
/// opacity fades, and Z-order all work natively because the video
/// composes into the WPF visual tree like any other image.
///
/// Pipeline: decoded BGRA (CPU or GPU texture) → upload into
/// <c>_uploadTexture</c> → <see cref="D3D11VideoScaler"/> letterboxes
/// into one of two shared-handle D3D11 textures
/// (<see cref="SharedTextureSlot"/>) → <c>context.Flush()</c> →
/// publish the written slot index as the "next to expose". A
/// <see cref="CompositionTarget.Rendering"/> handler on the UI thread
/// locks the <see cref="D3DImage"/>, swaps the back-buffer pointer to
/// the D3D9 surface that aliases the shared texture, and adds a dirty
/// rect. WPF composition samples the slot on the next monitor
/// refresh.
///
/// Two slots alternate each frame: one is currently bound to D3DImage
/// while the other accepts the next write. Without the double buffer,
/// the scaler's write and WPF's read would race on the same pixels
/// (D3D9 has no keyed-mutex interop).
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
            Stretch = Stretch.Fill,
            // Pixel-accurate sampling: the slot is already at display
            // pixel size (scaler letterboxed into it), so we just want
            // WPF to blit 1:1 without extra filtering.
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(_imageChild, BitmapScalingMode.NearestNeighbor);
        AddVisualChild(_imageChild);
        AddLogicalChild(_imageChild);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _pulseTimer = new System.Threading.Timer(
            _ => EmitPulse(), null,
            System.TimeSpan.FromSeconds(2),
            System.TimeSpan.FromSeconds(2));
    }

    private void EmitPulse()
    {
        long input, submissions;
        int attachedSlot, pendingSlot;
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }
            input = InputFrameCount;
            submissions = PresentSubmissionCount;
            attachedSlot = _attachedSlotIndex;
            pendingSlot = _pendingSlotIndex;
        }
        var dIn = input - _pulseLastInput; _pulseLastInput = input;
        var dSub = submissions - _pulseLastSubmissions; _pulseLastSubmissions = submissions;
        if (dIn == 0 && dSub == 0)
        {
            return;
        }
        VicoScreenShare.Client.Diagnostics.DebugLog.Write(
            $"[paint-pulse] 2s: input=+{dIn} exposed=+{dSub} attached={attachedSlot} pending={pendingSlot}");
    }

    /// <summary>
    /// Receives decoded BGRA frames from the network path.
    /// </summary>
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

    /// <summary>
    /// When non-null the renderer subscribes to this source instead of
    /// <see cref="Receiver"/>, so the streamer sees a self-preview of
    /// what they're sharing. Set to null to go back to the remote
    /// stream.
    /// </summary>
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
    /// Legacy property kept for XAML compatibility. Frame pacing is now
    /// owned by WPF composition (<see cref="CompositionTarget.Rendering"/>),
    /// not the renderer.
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
    /// Radius (in DIPs) of rounded corners. Implemented as a
    /// <see cref="RectangleGeometry"/> <see cref="UIElement.Clip"/>,
    /// not a child-HWND region, so fades / opacity / animations just
    /// work.
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

    // The Image child hosts our D3DImage. We own it as a single logical
    // and visual child so WPF measures / arranges / renders it as part
    // of our subtree.
    private readonly Image _imageChild;
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0
        ? _imageChild
        : throw new ArgumentOutOfRangeException(nameof(index));

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        _imageChild.Measure(availableSize);
        return _imageChild.DesiredSize;
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
        UpdateClipGeometry();
        ResizeSlotsIfNeeded();
    }

    // The D3D side — all fields touched from the decoder thread
    // (OnFrameArrived/OnTextureArrived) and/or the UI thread
    // (CompositionTarget.Rendering). _renderLock serializes.
    private readonly D3DImage _d3dImage;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _uploadTexture;
    private int _uploadWidth;
    private int _uploadHeight;
    private D3D11VideoScaler? _scaler;
    private D3D9ExBridge? _bridge;

    // Two-slot display ring. Each slot is a D3D11 shared texture
    // paired with the D3D9 surface that aliases its GPU memory. The
    // decoder thread scales into the non-attached slot; the UI thread
    // binds the newly-written slot to D3DImage on the next composition
    // tick.
    private readonly SharedTextureSlot?[] _slots = new SharedTextureSlot?[2];
    private int _slotWidth;
    private int _slotHeight;
    private int _attachedSlotIndex = -1;
    private int _pendingSlotIndex = -1;

    // Last painted source and its dimensions. Used to re-letterbox
    // into freshly-recreated slots after a resize when no new frame
    // has arrived (common on static sources like an idle browser
    // tab). The reference is non-owning — it points at _uploadTexture
    // which persists for the life of the renderer.
    private ID3D11Texture2D? _lastPaintedTexture;
    private int _lastPaintedSourceWidth;
    private int _lastPaintedSourceHeight;

    private ICaptureSource? _attachedReceiver;
    private ICaptureSource? _attachedLocalSource;
    private readonly object _renderLock = new();
    private bool _disposed;

    private System.Threading.Timer? _pulseTimer;
    private long _pulseLastInput;
    private long _pulseLastSubmissions;
    private long _frameFailureCount;

    /// <summary>Every frame that reached <see cref="OnFrameArrived"/> or
    /// <see cref="OnTextureArrived"/> from the decoder.</summary>
    public long InputFrameCount { get; private set; }

    /// <summary>D3DImage <c>SetBackBuffer + AddDirtyRect</c> calls — one
    /// per frame actually shown to WPF composition. Under contention a
    /// freshly-written slot can be overwritten by a newer frame before
    /// the UI thread exposes it, so this counts unique exposures, not
    /// scaler invocations.</summary>
    public long PresentSubmissionCount { get; private set; }

    /// <summary>In the D3DImage model there is no separate "DWM
    /// presented" count — WPF pulls from the attached slot at its own
    /// cadence. Returns the same value as
    /// <see cref="PresentSubmissionCount"/> for binding compatibility.</summary>
    public long PaintedFrameCount => PresentSubmissionCount;

    /// <summary>Frames scaled but overwritten in the display ring
    /// before the UI thread could expose them.</summary>
    public long DroppedFrameCount => InputFrameCount - PresentSubmissionCount;

    /// <summary>Wall-clock duration of the last UI-thread expose
    /// (Lock + SetBackBuffer + AddDirtyRect + Unlock).</summary>
    public double LastPaintMs { get; private set; }

    public (long Input, long Painted, double LastPaintMs) Snapshot()
    {
        lock (_renderLock)
        {
            return (InputFrameCount, PresentSubmissionCount, LastPaintMs);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshActiveFrameSource();
        // Subscribe on Loaded, not ctor, so we only tick while the
        // element is actually part of a live tree.
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        TeardownD3D();
    }

    private void TeardownD3D()
    {
        try { _pulseTimer?.Dispose(); } catch { }
        _pulseTimer = null;

        lock (_renderLock)
        {
            _disposed = true;
            DetachReceiver();
            DetachLocalSource();

            // Detach the D3DImage from whatever slot it's bound to
            // before we dispose the slot — SetBackBuffer must run on
            // the UI thread, which is exactly where Unloaded fires.
            try
            {
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            }
            catch { }
            finally
            {
                try { _d3dImage.Unlock(); } catch { }
            }

            _attachedSlotIndex = -1;
            _pendingSlotIndex = -1;
            _lastPaintedTexture = null;
            _lastPaintedSourceWidth = 0;
            _lastPaintedSourceHeight = 0;

            _scaler?.Dispose(); _scaler = null;
            _uploadTexture?.Dispose(); _uploadTexture = null;
            _uploadWidth = 0;
            _uploadHeight = 0;
            for (var i = 0; i < _slots.Length; i++)
            {
                _slots[i]?.Dispose();
                _slots[i] = null;
            }
            _slotWidth = 0;
            _slotHeight = 0;
            _bridge?.Dispose(); _bridge = null;

            // _device/_context are borrowed from App.SharedDevices; the
            // App exit path owns their lifetime.
            _context = null;
            _device = null;
        }
    }

    /// <summary>
    /// Lazy initialization: first frame wakes this up once WPF has laid
    /// us out AND the shared D3D11 device is ready. Called under
    /// <see cref="_renderLock"/>.
    /// </summary>
    private bool InitD3DLocked()
    {
        if (_bridge is not null && _device is not null)
        {
            return true;
        }

        var sharedDevices = App.SharedDevices;
        if (sharedDevices is null)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                "[renderer] App.SharedDevices is null — cannot init D3D");
            return false;
        }
        _device = sharedDevices.Device;
        _context = sharedDevices.Context;

        try
        {
            _bridge ??= new D3D9ExBridge();
            VicoScreenShare.Client.Diagnostics.DebugLog.Write("[renderer] D3D9Ex bridge created");
            return true;
        }
        catch (Exception ex)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                $"[renderer] D3D9Ex bridge create threw: {ex.Message}");
            _device = null;
            _context = null;
            return false;
        }
    }

    private void ResizeSlotsIfNeeded()
    {
        lock (_renderLock)
        {
            if (_disposed || _bridge is null || _device is null)
            {
                return;
            }

            var newW = Math.Max(1, (int)ActualWidth);
            var newH = Math.Max(1, (int)ActualHeight);
            if (newW == _slotWidth && newH == _slotHeight)
            {
                return;
            }

            // Detach D3DImage's back buffer and release the old slot
            // textures. Both the D3D11 and D3D9 sides point at shared
            // GPU memory; WPF may still hold a last reference via
            // composition, but the underlying ComPtr refcount sorts
            // that out. The key correctness bit is clearing
            // SetBackBuffer first so WPF stops sampling the old
            // surface before its COM ref drops.
            try
            {
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            }
            catch { }
            finally
            {
                try { _d3dImage.Unlock(); } catch { }
            }

            for (var i = 0; i < _slots.Length; i++)
            {
                _slots[i]?.Dispose();
                _slots[i] = null;
            }
            _attachedSlotIndex = -1;
            _pendingSlotIndex = -1;
            _slotWidth = newW;
            _slotHeight = newH;

            // Kill the cached scaler: its output view holds a
            // reference to whichever slot texture we passed in last,
            // which we just disposed.
            _scaler?.Dispose();
            _scaler = null;

            // Re-scale the last painted content into a fresh slot so a
            // static source doesn't show a blank frame after resize.
            if (_lastPaintedTexture is not null
                && _lastPaintedSourceWidth > 0
                && _lastPaintedSourceHeight > 0)
            {
                try
                {
                    PaintSourceToSlotLocked(
                        _lastPaintedTexture,
                        _lastPaintedSourceWidth,
                        _lastPaintedSourceHeight);
                }
                catch (Exception ex)
                {
                    VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                        $"[renderer] post-resize repaint threw: {ex.Message}");
                }
            }
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

    /// <summary>
    /// Picks which frame source feeds the renderer: local capture
    /// takes precedence, remote receiver otherwise. Detaches first so
    /// we never have both pumping <see cref="OnFrameArrived"/>.
    /// </summary>
    private void RefreshActiveFrameSource()
    {
        DetachReceiver();
        DetachLocalSource();
        if (!IsLoaded)
        {
            // OnLoaded will call us again with both DP values already
            // in place.
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
        VicoScreenShare.Client.Diagnostics.DebugLog.Write(
            $"[renderer] subscribed to FrameArrived + TextureArrived (receiver={receiver.GetType().Name})");
    }

    private void DetachReceiver()
    {
        if (_attachedReceiver is null)
        {
            return;
        }

        try { _attachedReceiver.FrameArrived -= OnFrameArrived; } catch { }
        try { _attachedReceiver.TextureArrived -= OnTextureArrived; } catch { }
        _attachedReceiver = null;
        VicoScreenShare.Client.Diagnostics.DebugLog.Write("[renderer] unsubscribed FrameArrived + TextureArrived");
    }

    private void AttachLocalSource(ICaptureSource source)
    {
        _attachedLocalSource = source;
        source.FrameArrived += OnFrameArrived;
        source.TextureArrived += OnTextureArrived;
        VicoScreenShare.Client.Diagnostics.DebugLog.Write(
            "[renderer] subscribed to ICaptureSource.FrameArrived + TextureArrived (local preview)");
    }

    private void DetachLocalSource()
    {
        if (_attachedLocalSource is null)
        {
            return;
        }

        try { _attachedLocalSource.FrameArrived -= OnFrameArrived; } catch { }
        try { _attachedLocalSource.TextureArrived -= OnTextureArrived; } catch { }
        _attachedLocalSource = null;
        VicoScreenShare.Client.Diagnostics.DebugLog.Write(
            "[renderer] unsubscribed ICaptureSource.FrameArrived + TextureArrived (local preview)");
    }

    private void OnFrameArrived(in CaptureFrameData frame)
    {
        if (frame.Format != CaptureFramePixelFormat.Bgra8)
        {
            return;
        }

        var paintStart = System.Diagnostics.Stopwatch.GetTimestamp();
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

                var width = frame.Width;
                var height = frame.Height;
                if (width <= 0 || height <= 0)
                {
                    return;
                }

                EnsureUploadTextureLocked(width, height);

                unsafe
                {
                    fixed (byte* src = frame.Pixels)
                    {
                        _context!.UpdateSubresource(
                            _uploadTexture!,
                            0,
                            null,
                            (IntPtr)src,
                            (uint)frame.StrideBytes,
                            (uint)(frame.StrideBytes * height));
                    }
                }

                PaintSourceToSlotLocked(_uploadTexture!, width, height);
            }
        }
        catch (Exception ex)
        {
            var count = System.Threading.Interlocked.Increment(ref _frameFailureCount);
            if (count <= 5 || count % 60 == 0)
            {
                VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                    $"[renderer] OnFrameArrived threw (#{count}): {ex.GetType().Name}: {ex.Message}");
            }
        }
        var paintEnd = System.Diagnostics.Stopwatch.GetTimestamp();
        // Include the scaler+flush in the CPU-path paint measurement.
        // UI-thread expose timing is measured separately in
        // OnRendering.
        LastPaintMs = (paintEnd - paintStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void OnTextureArrived(IntPtr nativeTexture, int width, int height, TimeSpan timestamp)
    {
        if (nativeTexture == IntPtr.Zero || width <= 0 || height <= 0)
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
                if (!InitD3DLocked())
                {
                    return;
                }

                InputFrameCount++;

                // GPU-copy the decoder's texture into our owned
                // upload texture so the decoder can safely reuse its
                // texture on the next decode cycle. CopyResource is a
                // GPU-side DMA: no CPU stall, no PCIe traffic.
                EnsureUploadTextureLocked(width, height);
                using var sourceTexture = new ID3D11Texture2D(nativeTexture);
                _context!.CopyResource(_uploadTexture!, sourceTexture);

                PaintSourceToSlotLocked(_uploadTexture!, width, height);
            }
        }
        catch (Exception ex)
        {
            var count = System.Threading.Interlocked.Increment(ref _frameFailureCount);
            if (count <= 5 || count % 60 == 0)
            {
                VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                    $"[renderer] OnTextureArrived threw (#{count}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Scale the source texture into the next free display slot and
    /// publish the slot index. Called under <see cref="_renderLock"/>.
    /// </summary>
    private void PaintSourceToSlotLocked(ID3D11Texture2D source, int srcW, int srcH)
    {
        if (_bridge is null || _device is null || _context is null)
        {
            return;
        }

        // If we haven't been laid out yet, pick a provisional slot
        // size so the first frame doesn't sit gated behind an arrange
        // pass. OnRenderSizeChanged will rebuild slots at the real
        // display pixel size once WPF arranges us.
        if (_slotWidth <= 0 || _slotHeight <= 0)
        {
            _slotWidth = Math.Max(1, (int)ActualWidth);
            _slotHeight = Math.Max(1, (int)ActualHeight);
            if (_slotWidth <= 1 || _slotHeight <= 1)
            {
                _slotWidth = 1280;
                _slotHeight = 720;
            }
        }

        // Pick a write slot that is not currently attached to D3DImage.
        // Two slots + this rule means the WPF composition thread is
        // always sampling a surface we're not writing to. If nothing is
        // attached yet (first frame), slot 0 is free.
        var writeSlot = _attachedSlotIndex == -1 ? 0 : _attachedSlotIndex ^ 1;
        EnsureSlotLocked(writeSlot);
        if (_slots[writeSlot] is null)
        {
            return;
        }

        EnsureScalerLocked(srcW, srcH, _slotWidth, _slotHeight);
        var destRect = ComputeLetterboxRect(srcW, srcH, _slotWidth, _slotHeight);
        try
        {
            _scaler!.Process(source, _slots[writeSlot]!.D3D11Texture, destRect);
        }
        catch (Exception ex)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                $"[renderer] scaler.Process threw: {ex.Message}");
            return;
        }

        // Flush the D3D11 command queue so writes land in GPU memory
        // before the UI thread AddDirtyRects WPF into sampling this
        // surface. Without Flush the scaler's commands might still be
        // queued when WPF reads.
        _context.Flush();

        _pendingSlotIndex = writeSlot;
        _lastPaintedTexture = source;
        _lastPaintedSourceWidth = srcW;
        _lastPaintedSourceHeight = srcH;
    }

    private void EnsureSlotLocked(int slotIndex)
    {
        if (_slots[slotIndex] is not null
            && _slots[slotIndex]!.Width == _slotWidth
            && _slots[slotIndex]!.Height == _slotHeight)
        {
            return;
        }
        _slots[slotIndex]?.Dispose();
        try
        {
            _slots[slotIndex] = new SharedTextureSlot(_device!, _bridge!, _slotWidth, _slotHeight);
        }
        catch (Exception ex)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                $"[renderer] slot create threw: {ex.Message}");
            _slots[slotIndex] = null;
        }
    }

    private void EnsureUploadTextureLocked(int width, int height)
    {
        if (_uploadTexture is not null && _uploadWidth == width && _uploadHeight == height)
        {
            return;
        }

        if (ReferenceEquals(_lastPaintedTexture, _uploadTexture))
        {
            _lastPaintedTexture = null;
        }
        _uploadTexture?.Dispose();
        // DEFAULT usage + ShaderResource + RenderTarget. D3D11 Video
        // Processor rejects input with E_INVALIDARG unless the source
        // texture has RenderTarget bind (even though we never actually
        // use the view, the runtime validates the bind-flag
        // combination before creating the input view).
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _uploadTexture = _device!.CreateTexture2D(desc);
        _uploadWidth = width;
        _uploadHeight = height;
        _scaler?.Dispose();
        _scaler = null;
    }

    private void EnsureScalerLocked(int srcW, int srcH, int dstW, int dstH)
    {
        if (_scaler is not null
            && _scaler.SourceWidth == srcW
            && _scaler.SourceHeight == srcH
            && _scaler.DestWidth == dstW
            && _scaler.DestHeight == dstH)
        {
            return;
        }
        _scaler?.Dispose();
        _scaler = new D3D11VideoScaler(_device!, srcW, srcH, dstW, dstH);
    }

    private static RawRect ComputeLetterboxRect(int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
        {
            return new RawRect(0, 0, Math.Max(1, dstW), Math.Max(1, dstH));
        }

        var srcAspect = (double)srcW / srcH;
        var dstAspect = (double)dstW / dstH;

        if (srcAspect > dstAspect)
        {
            var h = (int)Math.Round(dstW / srcAspect);
            var y = (dstH - h) / 2;
            return new RawRect(0, y, dstW, y + h);
        }
        else
        {
            var w = (int)Math.Round(dstH * srcAspect);
            var x = (dstW - w) / 2;
            return new RawRect(x, 0, x + w, dstH);
        }
    }

    /// <summary>
    /// UI-thread composition tick. If the decoder thread has written a
    /// fresh slot since last tick, bind it to D3DImage. WPF composition
    /// samples the new surface on this same refresh.
    /// </summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_d3dImage.IsFrontBufferAvailable)
        {
            return;
        }

        SharedTextureSlot? slot = null;
        int slotToExpose;
        lock (_renderLock)
        {
            if (_disposed || _pendingSlotIndex < 0)
            {
                return;
            }
            slotToExpose = _pendingSlotIndex;
            _pendingSlotIndex = -1;
            slot = _slots[slotToExpose];
            if (slot is null)
            {
                return;
            }

            var paintStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(
                    D3DResourceType.IDirect3DSurface9,
                    slot.D3D9Surface.NativePointer);
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, slot.Width, slot.Height));
                _attachedSlotIndex = slotToExpose;
                PresentSubmissionCount++;
            }
            catch (Exception ex)
            {
                VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                    $"[renderer] D3DImage expose threw: {ex.Message}");
            }
            finally
            {
                try { _d3dImage.Unlock(); } catch { }
            }
            var paintEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            LastPaintMs = (paintEnd - paintStart) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// When the OS suspends GPU composition (lock screen, remote
    /// session, DWM restart) the D3DImage drops its front buffer. On
    /// the recovery edge we re-attach the current slot; there's no
    /// useful work to do on the loss edge.
    /// </summary>
    private void OnFrontBufferAvailableChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_d3dImage.IsFrontBufferAvailable)
        {
            return;
        }

        lock (_renderLock)
        {
            if (_disposed || _attachedSlotIndex < 0)
            {
                return;
            }
            var slot = _slots[_attachedSlotIndex];
            if (slot is null)
            {
                return;
            }
            try
            {
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(
                    D3DResourceType.IDirect3DSurface9,
                    slot.D3D9Surface.NativePointer);
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, slot.Width, slot.Height));
            }
            catch (Exception ex)
            {
                VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                    $"[renderer] front-buffer reattach threw: {ex.Message}");
            }
            finally
            {
                try { _d3dImage.Unlock(); } catch { }
            }
        }
    }
}
