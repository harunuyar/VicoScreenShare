namespace VicoScreenShare.Desktop.App.Rendering;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Windows.Direct3D;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

/// <summary>
/// Hosts a child Win32 HWND with its own DXGI swap chain and presents
/// decoded video frames into it. The name is a nod to WPF's D3DImage —
/// in practice we bypass D3DImage entirely because its D3D9-interop
/// requirement costs us a D3D9↔D3D11 shared-handle dance and we don't
/// need anything from it that a plain child HWND + swap chain doesn't
/// give us. WPF's airspace caveat (the hosted region can't overlap
/// other WPF visuals) is a non-issue for a dedicated video tile.
///
/// Pipeline: <see cref="StreamReceiver.FrameArrived"/> → upload BGRA
/// bytes into a D3D11 DYNAMIC texture → <see cref="D3D11VideoScaler"/>
/// → swap chain back buffer → <c>Present(1, ...)</c> at display vsync.
/// Runs entirely on the RTP/decoder thread, never touches the WPF UI
/// thread after construction.
///
/// The per-frame CPU upload is still there (decoder currently reads
/// back to <c>byte[]</c> inside <see cref="CaptureFrameData"/>). A
/// follow-up can expose the decoder's BGRA D3D11 texture directly,
/// skipping the upload for a true zero-copy path.
/// </summary>
public sealed class D3DImageVideoRenderer : HwndHost
{
    public D3DImageVideoRenderer()
    {
        _playoutQueue = new TimestampedFrameQueue(App.ReceiveBufferFrames);
        _presentLoop = new PresentLoop(_playoutQueue, PaintFromQueue);
    }

    // The receiver DP is typed as ICaptureSource so anything that
    // produces BGRA frames (StreamReceiver from the network path, or
    // the capture-test encode/decode bridge that wraps CaptureStreamer
    // + a local decoder) can plug in here unchanged. StreamReceiver
    // implements ICaptureSource so RoomView's existing binding to a
    // StreamReceiver instance keeps working.
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
    /// Optional local capture source — when non-null, the renderer
    /// subscribes to <see cref="ICaptureSource.FrameArrived"/> on this
    /// source instead of <see cref="Receiver"/>, so the streamer sees
    /// a self-preview of what they're sharing. Set to null to go back
    /// to rendering the remote stream.
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
    /// Cadence the receiver should paint at, in frames per second.
    /// Should match the sender's nominal frame rate from its
    /// <see cref="VicoScreenShare.Protocol.Messages.StreamStarted"/>
    /// message. Default 60.
    /// </summary>
    public static readonly DependencyProperty NominalFrameRateProperty =
        DependencyProperty.Register(
            nameof(NominalFrameRate),
            typeof(int),
            typeof(D3DImageVideoRenderer),
            new PropertyMetadata(60, OnNominalFrameRateChanged));

    public int NominalFrameRate
    {
        get => (int)GetValue(NominalFrameRateProperty);
        set => SetValue(NominalFrameRateProperty, value);
    }

    private static void OnNominalFrameRateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // The present loop is now driven entirely by content timestamps
        // (anchorWall + pts - anchorPts), not a target fps. This hook
        // is retained for API compatibility with existing XAML bindings
        // but has no runtime effect.
    }

    /// <summary>
    /// Radius (in DIPs) of rounded corners applied to the renderer's HWND
    /// via <see cref="SetWindowRgn"/>. Zero = rectangular. Because HwndHost
    /// cannot be clipped by WPF, the only way to get rounded video corners
    /// is to shape the HWND itself — and this shaping is what lets WPF
    /// overlays registered via <see cref="CutoutForProperty"/> show through.
    /// </summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(double),
            typeof(D3DImageVideoRenderer),
            new PropertyMetadata(0.0, OnRegionInputChanged));

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>
    /// Attached property: marks any <see cref="FrameworkElement"/> as a
    /// cutout for the targeted <see cref="D3DImageVideoRenderer"/>. The
    /// renderer computes the element's bounding rect in its own local
    /// coordinates and subtracts it from the HWND region, so the element's
    /// WPF pixels show through the hole in the child HWND.
    /// <para>
    /// Usage: <c>rendering:D3DImageVideoRenderer.CutoutFor="{Binding ElementName=MyRenderer}"</c>
    /// on a Border (or any FrameworkElement) laid out in the same visual
    /// tree subtree as the renderer. The cutout tracks the element's
    /// layout — moving, resizing, or visibility-toggling the element
    /// automatically updates the HWND region.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty CutoutForProperty =
        DependencyProperty.RegisterAttached(
            "CutoutFor",
            typeof(D3DImageVideoRenderer),
            typeof(D3DImageVideoRenderer),
            new PropertyMetadata(null, OnCutoutForChanged));

    public static void SetCutoutFor(FrameworkElement element, D3DImageVideoRenderer value)
        => element.SetValue(CutoutForProperty, value);

    public static D3DImageVideoRenderer? GetCutoutFor(FrameworkElement element)
        => (D3DImageVideoRenderer?)element.GetValue(CutoutForProperty);

    private static void OnCutoutForChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
        {
            return;
        }

        if (e.OldValue is D3DImageVideoRenderer old)
        {
            old.UnregisterCutoutElement(fe);
        }

        if (e.NewValue is D3DImageVideoRenderer @new)
        {
            @new.RegisterCutoutElement(fe);
        }
    }

    /// <summary>
    /// Attached property: when true, this element becomes a cutout for
    /// EVERY <see cref="D3DImageVideoRenderer"/> in the app — existing
    /// and future. Needed when a WPF overlay sits above multiple video
    /// surfaces (e.g. the self-preview's chrome strip, which needs to
    /// show through both the preview renderer AND any publisher tile
    /// renderer whose HWND covers the same screen region).
    /// </summary>
    public static readonly DependencyProperty CutoutForAllProperty =
        DependencyProperty.RegisterAttached(
            "CutoutForAll",
            typeof(bool),
            typeof(D3DImageVideoRenderer),
            new PropertyMetadata(false, OnCutoutForAllChanged));

    public static void SetCutoutForAll(FrameworkElement element, bool value)
        => element.SetValue(CutoutForAllProperty, value);

    public static bool GetCutoutForAll(FrameworkElement element)
        => (bool)element.GetValue(CutoutForAllProperty);

    private static readonly System.Collections.Generic.List<FrameworkElement> _globalCutoutElements = new();

    private static void OnCutoutForAllChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
        {
            return;
        }

        if (e.NewValue is true)
        {
            if (!_globalCutoutElements.Contains(fe))
            {
                _globalCutoutElements.Add(fe);
                fe.LayoutUpdated += OnGlobalCutoutLayoutUpdated;
                fe.IsVisibleChanged += OnGlobalCutoutVisibilityChanged;
                QueueGlobalRegionUpdate();
            }
        }
        else
        {
            if (_globalCutoutElements.Remove(fe))
            {
                fe.LayoutUpdated -= OnGlobalCutoutLayoutUpdated;
                fe.IsVisibleChanged -= OnGlobalCutoutVisibilityChanged;
                QueueGlobalRegionUpdate();
            }
        }
    }

    private static void OnGlobalCutoutLayoutUpdated(object? sender, EventArgs e) => QueueGlobalRegionUpdate();
    private static void OnGlobalCutoutVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueGlobalRegionUpdate();

    /// <summary>
    /// Nudge every live renderer to rebuild its region when the global
    /// cutout set changes. Each renderer's QueueRegionUpdate coalesces on
    /// DispatcherPriority.Render so N renderers don't each trigger N rebuilds.
    /// </summary>
    private static void QueueGlobalRegionUpdate()
    {
        foreach (var renderer in _instancesByHwnd.Values)
        {
            renderer.QueueRegionUpdate();
        }
    }

    private static void OnRegionInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is D3DImageVideoRenderer self)
        {
            self.QueueRegionUpdate();
        }
    }

    // Elements registered via CutoutFor. Each element's LayoutUpdated
    // triggers a region rebuild so the HWND hole tracks the WPF overlay
    // as it gets measured / resized / positioned.
    private readonly System.Collections.Generic.List<FrameworkElement> _cutoutElements = new();
    private bool _regionUpdateQueued;

    private void RegisterCutoutElement(FrameworkElement element)
    {
        if (_cutoutElements.Contains(element))
        {
            return;
        }

        _cutoutElements.Add(element);
        element.LayoutUpdated += OnCutoutLayoutUpdated;
        element.IsVisibleChanged += OnCutoutVisibilityChanged;
        QueueRegionUpdate();
    }

    private void UnregisterCutoutElement(FrameworkElement element)
    {
        if (!_cutoutElements.Remove(element))
        {
            return;
        }

        element.LayoutUpdated -= OnCutoutLayoutUpdated;
        element.IsVisibleChanged -= OnCutoutVisibilityChanged;
        QueueRegionUpdate();
    }

    private void OnCutoutLayoutUpdated(object? sender, EventArgs e) => QueueRegionUpdate();
    private void OnCutoutVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueRegionUpdate();

    /// <summary>
    /// Coalesce region rebuilds into a single dispatcher callback. Layout
    /// passes can fire LayoutUpdated many times per frame; rebuilding the
    /// region every time is wasteful and flickers visually.
    /// </summary>
    private void QueueRegionUpdate()
    {
        if (_regionUpdateQueued)
        {
            return;
        }

        _regionUpdateQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _regionUpdateQueued = false;
            UpdateWindowRegion();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// Apply a <see cref="SetWindowRgn"/> to the HWND: base shape is a
    /// rounded rect of the current bounds (CornerRadius applied), minus
    /// the bounding rect of every registered cutout element. The result
    /// is the shape of pixels actually drawn by the swap chain; anywhere
    /// outside this shape, WPF's rendering of the parent HWND shows —
    /// which is how display-name pills and hover buttons appear on top.
    /// </summary>
    private void UpdateWindowRegion()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var w = (int)Math.Max(1, ActualWidth);
        var h = (int)Math.Max(1, ActualHeight);

        // DIP-to-pixel scaling for GDI — WPF sizes are DIPs; SetWindowRgn
        // wants device pixels. PresentationSource.CompositionTarget gives
        // the DPI scaling matrix we need.
        var scale = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            scale = source.CompositionTarget.TransformToDevice.M11;
        }

        IntPtr baseRgn;
        var cornerPx = (int)Math.Round(CornerRadius * scale * 2); // CreateRoundRectRgn takes diameter
        if (cornerPx > 0)
        {
            baseRgn = CreateRoundRectRgn(
                0, 0,
                (int)Math.Round(w * scale) + 1,
                (int)Math.Round(h * scale) + 1,
                cornerPx, cornerPx);
        }
        else
        {
            baseRgn = CreateRectRgn(0, 0,
                (int)Math.Round(w * scale),
                (int)Math.Round(h * scale));
        }

        if (baseRgn == IntPtr.Zero)
        {
            return;
        }

        // Union of per-renderer + global cutouts. Global cutouts let a WPF
        // overlay (e.g. self-preview's chrome strip) punch holes in every
        // renderer at once, so it's visible no matter which HWND happens
        // to be on top in that screen region.
        System.Collections.Generic.IEnumerable<FrameworkElement> allCutouts = _globalCutoutElements.Count == 0
            ? _cutoutElements
            : System.Linq.Enumerable.Concat(_cutoutElements, _globalCutoutElements);

        foreach (var element in allCutouts)
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                continue;
            }

            try
            {
                var transform = element.TransformToVisual(this);
                var rect = transform.TransformBounds(new System.Windows.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                var cutRgn = CreateRectRgn(
                    (int)Math.Round(rect.Left * scale),
                    (int)Math.Round(rect.Top * scale),
                    (int)Math.Round(rect.Right * scale),
                    (int)Math.Round(rect.Bottom * scale));
                if (cutRgn != IntPtr.Zero)
                {
                    CombineRgn(baseRgn, baseRgn, cutRgn, RGN_DIFF);
                    DeleteObject(cutRgn);
                }
            }
            catch
            {
                // TransformToVisual throws if element isn't connected to the
                // same visual tree as us — skip silently.
            }
        }

        // SetWindowRgn takes ownership of the region; no DeleteObject after
        // this call succeeds. WPF caller loses the handle which is fine.
        SetWindowRgn(_hwnd, baseRgn, true);
    }

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const string WindowClassName = "ScreenSharingVideoHost";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string? lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    private const int RGN_AND = 1;
    private const int RGN_OR = 2;
    private const int RGN_XOR = 3;
    private const int RGN_DIFF = 4;
    private const int RGN_COPY = 5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    // IDC_HAND — hand cursor for clickable content. Stored in WNDCLASSEX so
    // Windows sets it automatically on every WM_SETCURSOR in the renderer's
    // HWND area, keeping the cursor consistent no matter which child HWND
    // the pointer is over.
    private static readonly IntPtr IDC_HAND = new IntPtr(32649);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    private const int BLACK_BRUSH = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private static readonly object ClassRegLock = new();
    private static bool _classRegistered;
    private static GCHandle _wndProcHandle;

    // hwnd → renderer instance. The shared window-class WndProc looks up
    // the owning instance to dispatch mouse input back into WPF; without
    // this, WM_LBUTTONDOWN / MOUSEMOVE never leave the child HWND and
    // overlays sitting on the video never receive clicks or drag.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, D3DImageVideoRenderer> _instancesByHwnd = new();

    // Mouse message constants.
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    /// <summary>
    /// Fired when the child window receives <c>WM_LBUTTONDOWN</c>. The
    /// <see cref="MouseButtonEventArgs"/>-style payload reports the
    /// click position in the renderer's local DIP coordinates.
    /// </summary>
    public event EventHandler<VideoMouseEventArgs>? VideoMouseDown;
    public event EventHandler<VideoMouseEventArgs>? VideoMouseMove;
    public event EventHandler<VideoMouseEventArgs>? VideoMouseUp;
    public event EventHandler<VideoMouseEventArgs>? VideoMouseDoubleClick;

    /// <summary>
    /// Command invoked on <c>WM_LBUTTONUP</c> when it pairs with a prior
    /// <c>WM_LBUTTONDOWN</c> on this window — a completed click. Tiles
    /// bind this to <c>FocusTileCommand</c> so clicking anywhere on the
    /// video focuses it, not just the few pixels of WPF chrome.
    /// </summary>
    public static readonly DependencyProperty LeftClickCommandProperty =
        DependencyProperty.Register(nameof(LeftClickCommand), typeof(System.Windows.Input.ICommand),
            typeof(D3DImageVideoRenderer), new PropertyMetadata(null));

    public System.Windows.Input.ICommand? LeftClickCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(LeftClickCommandProperty);
        set => SetValue(LeftClickCommandProperty, value);
    }

    public static readonly DependencyProperty LeftClickCommandParameterProperty =
        DependencyProperty.Register(nameof(LeftClickCommandParameter), typeof(object),
            typeof(D3DImageVideoRenderer), new PropertyMetadata(null));

    public object? LeftClickCommandParameter
    {
        get => GetValue(LeftClickCommandParameterProperty);
        set => SetValue(LeftClickCommandParameterProperty, value);
    }

    public static readonly DependencyProperty LeftDoubleClickCommandProperty =
        DependencyProperty.Register(nameof(LeftDoubleClickCommand), typeof(System.Windows.Input.ICommand),
            typeof(D3DImageVideoRenderer), new PropertyMetadata(null));

    public System.Windows.Input.ICommand? LeftDoubleClickCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(LeftDoubleClickCommandProperty);
        set => SetValue(LeftDoubleClickCommandProperty, value);
    }

    public static readonly DependencyProperty LeftDoubleClickCommandParameterProperty =
        DependencyProperty.Register(nameof(LeftDoubleClickCommandParameter), typeof(object),
            typeof(D3DImageVideoRenderer), new PropertyMetadata(null));

    public object? LeftDoubleClickCommandParameter
    {
        get => GetValue(LeftDoubleClickCommandParameterProperty);
        set => SetValue(LeftDoubleClickCommandParameterProperty, value);
    }

    // Drag-detection state: WM_LBUTTONDOWN sets _lbuttonDownInside, WM_LBUTTONUP
    // clears it and fires LeftClickCommand iff we never left the window.
    private bool _lbuttonDownInside;

    /// <summary>
    /// Keep this renderer's child HWND at the top of its sibling z-order,
    /// even when a newer sibling HWND is created. Child windows z-order by
    /// creation time; without this flag, a publisher tile mounted AFTER
    /// the self-preview would cover the preview in its area. Setting this
    /// to true on the self-preview makes <see cref="BuildWindowCore"/>
    /// re-raise it whenever any other renderer HWND is created.
    /// </summary>
    public static readonly DependencyProperty IsAlwaysTopmostProperty =
        DependencyProperty.Register(nameof(IsAlwaysTopmost), typeof(bool),
            typeof(D3DImageVideoRenderer), new PropertyMetadata(false));

    public bool IsAlwaysTopmost
    {
        get => (bool)GetValue(IsAlwaysTopmostProperty);
        set => SetValue(IsAlwaysTopmostProperty, value);
    }

    private IntPtr _hwnd;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _uploadTexture;
    private D3D11VideoScaler? _scaler;
    private int _uploadWidth;
    private int _uploadHeight;

    // GPU texture ring: owned BGRA textures used as the hand-off buffer
    // for the GPU decode path. OnTextureArrived GPU-copies the decoder's
    // texture into one of these slots and enqueues the slot index onto the
    // playout queue; PaintFromQueue dequeues and paints from the slot on
    // the present thread, then releases the slot back to the pool. This
    // is what makes ReceiveBufferFrames work on the GPU path — without
    // the ring we'd have to either paint inline (bypassing the buffer) or
    // round-trip through byte[] (killing GPU perf).
    //
    // Lazy-allocated: size matches _playoutQueue.MaxCapacity, but each
    // slot's underlying texture is only created on first use. In steady
    // state with ReceiveBufferFrames=5, only ~5-10 slots are ever hot.
    private ID3D11Texture2D?[]? _textureRing;
    private int[]? _ringSlotWidth;
    private int[]? _ringSlotHeight;
    private bool[]? _ringSlotInUse;
    private int _swapChainWidth;
    private int _swapChainHeight;
    private ICaptureSource? _attachedReceiver;
    private ICaptureSource? _attachedLocalSource;
    private readonly object _renderLock = new();
    private bool _disposed;

    // Receive-side jitter buffer + paint pacer. OnFrameArrived enqueues
    // on the decoder thread; the buffer's render thread pops one frame
    // per tick (tick = 1/NominalFrameRate) and calls PaintPaced. The
    // self-preview path bypasses the buffer entirely (it's not the path
    // the user actually watches).
    private readonly TimestampedFrameQueue _playoutQueue;
    private readonly PresentLoop _presentLoop;

    // Diagnostics for the stats overlay. Tracks both sides of the coin:
    //   InputFrameCount    — every frame that reached OnFrameArrived from
    //                        the decoder (before any render work).
    //   PaintedFrameCount  — every frame that actually made it through
    //                        the upload + scaler + Present cycle.
    //   LastPaintMs        — wall-clock time of the last paint.
    public long InputFrameCount { get; private set; }
    public long PaintedFrameCount { get; private set; }
    public long DroppedFrameCount => InputFrameCount - PaintedFrameCount;
    public double LastPaintMs { get; private set; }

    /// <summary>
    /// Atomic snapshot of the three counters. Taken under the render
    /// lock so readers see a consistent "input N / painted N" pair
    /// instead of catching an in-flight frame mid-increment (which
    /// would falsely inflate the "dropped" count).
    /// </summary>
    public (long Input, long Painted, double LastPaintMs) Snapshot()
    {
        lock (_renderLock)
        {
            return (InputFrameCount, PaintedFrameCount, LastPaintMs);
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClassRegistered();

        _hwnd = CreateWindowExW(
            0, WindowClassName, null,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write("[renderer] CreateWindowEx failed for video host");
            throw new InvalidOperationException("CreateWindowEx failed for video host");
        }

        VicoScreenShare.Client.Diagnostics.DebugLog.Write($"[renderer] BuildWindowCore hwnd=0x{_hwnd.ToInt64():X}");

        // Register for mouse-message dispatch so clicks on the video
        // reach WPF via LeftClickCommand / VideoMouse* events.
        _instancesByHwnd[_hwnd] = this;

        // Child HWND z-order follows creation order: a brand-new HWND sits
        // on top of its siblings. If any renderer is marked IsAlwaysTopmost
        // (the self-preview), re-raise it now so incoming tile HWNDs don't
        // cover it.
        foreach (var kv in _instancesByHwnd)
        {
            if (kv.Value != this && kv.Value.IsAlwaysTopmost && kv.Value._hwnd != IntPtr.Zero)
            {
                SetWindowPos(kv.Value._hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        // Do NOT create the swap chain here — ActualWidth/Height are 0
        // before WPF has laid the element out. InitD3D runs lazily on
        // the first OnFrameArrived call, when we know both the window
        // size (via Arrange) and the frame size.
        _presentLoop.Start();
        RefreshActiveFrameSource();
        // Pick up any existing global cutouts on this fresh renderer.
        // Without this, a tile mounted AFTER a global cutout is registered
        // would render over that cutout area until the next layout tick.
        QueueRegionUpdate();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Stop the present loop BEFORE tearing D3D state. The loop calls
        // PaintFromQueue → upload + scale + Present which touches
        // _swapChain / _scaler / _uploadTexture; disposing those while
        // the thread is mid-paint would race. PresentLoop.Dispose joins
        // the thread so the D3D teardown below is unopposed.
        _presentLoop.Dispose();
        _playoutQueue.Clear();
        lock (_renderLock)
        {
            _disposed = true;
            DetachReceiver();
            DetachLocalSource();
            _scaler?.Dispose(); _scaler = null;
            _uploadTexture?.Dispose(); _uploadTexture = null;
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
            _swapChain?.Dispose(); _swapChain = null;
            // _device and _context are borrowed from App.SharedDevices.
            // This renderer can be instantiated more than once (main
            // tile + self-preview tile), so we must NOT dispose them —
            // the other instance is still using them and the App exit
            // path is the one that actually owns their lifetime.
            _context = null;
            _device = null;
        }
        if (_hwnd != IntPtr.Zero)
        {
            _instancesByHwnd.TryRemove(_hwnd, out _);
            DestroyWindow(_hwnd);
        }
        _hwnd = IntPtr.Zero;
    }

    private void InitD3DLocked()
    {
        if (_swapChain is not null)
        {
            return;
        }

        var sharedDevices = App.SharedDevices;
        if (sharedDevices is null)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write("[renderer] App.SharedDevices is null — cannot init D3D");
            return;
        }
        _device = sharedDevices.Device;
        _context = sharedDevices.Context;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        // Pick an initial size from whatever WPF has already measured.
        // If layout hasn't run yet (ActualWidth == 0), fall back to
        // 1280x720 — OnRenderSizeChanged will ResizeBuffers to the real
        // size the moment WPF finishes laying out the parent Grid.
        _swapChainWidth = Math.Max(1, (int)ActualWidth);
        _swapChainHeight = Math.Max(1, (int)ActualHeight);
        if (_swapChainWidth <= 1 || _swapChainHeight <= 1)
        {
            _swapChainWidth = 1280;
            _swapChainHeight = 720;
        }

        var desc = new SwapChainDescription1
        {
            Width = (uint)_swapChainWidth,
            Height = (uint)_swapChainHeight,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Unspecified,
        };

        try
        {
            _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
            VicoScreenShare.Client.Diagnostics.DebugLog.Write($"[renderer] swap chain created {_swapChainWidth}x{_swapChainHeight}");
        }
        catch (Exception ex)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write($"[renderer] CreateSwapChainForHwnd threw: {ex.Message}");
            _device = null;
            _context = null;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        QueueRegionUpdate();
        lock (_renderLock)
        {
            if (_disposed || _swapChain is null)
            {
                return;
            }

            var newW = Math.Max(1, (int)ActualWidth);
            var newH = Math.Max(1, (int)ActualHeight);
            if (newW == _swapChainWidth && newH == _swapChainHeight)
            {
                return;
            }

            // Order matters: every D3D reference to the swap chain's
            // back buffers has to be released BEFORE ResizeBuffers. The
            // scaler's cached output view holds one, so dispose the
            // scaler first. Then resize. If that still fails (happens
            // when Windows maximizes the window and DXGI rejects the
            // new size for some reason), nuke the whole swap chain and
            // let InitD3DLocked rebuild from scratch on the next frame.
            _scaler?.Dispose();
            _scaler = null;

            try
            {
                _swapChain.ResizeBuffers(
                    2, (uint)newW, (uint)newH,
                    Format.B8G8R8A8_UNorm, SwapChainFlags.None);
                VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                    $"[renderer] swap chain resized {_swapChainWidth}x{_swapChainHeight} -> {newW}x{newH}");
                _swapChainWidth = newW;
                _swapChainHeight = newH;
            }
            catch (Exception ex)
            {
                VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                    $"[renderer] ResizeBuffers failed ({ex.Message}) — rebuilding swap chain");
                try { _swapChain.Dispose(); } catch { }
                _swapChain = null;
                _uploadTexture?.Dispose();
                _uploadTexture = null;
                _uploadWidth = 0;
                _uploadHeight = 0;
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
    /// Picks which frame source feeds the renderer: local capture takes
    /// precedence (so a streamer sees their own screen), remote receiver
    /// otherwise. Detaches from the previously-active source first so we
    /// never have both pumping the same <see cref="OnFrameArrived"/>.
    /// </summary>
    private void RefreshActiveFrameSource()
    {
        DetachReceiver();
        DetachLocalSource();
        if (_hwnd == IntPtr.Zero)
        {
            // HwndHost hasn't built yet — BuildWindowCore will call us
            // again with both DP values already in place.
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
        VicoScreenShare.Client.Diagnostics.DebugLog.Write($"[renderer] subscribed to FrameArrived + TextureArrived (receiver={receiver.GetType().Name})");
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
        // Drop any queued frames so a fresh attach doesn't play back
        // stale content from the previous stream.
        _playoutQueue.Clear();
        VicoScreenShare.Client.Diagnostics.DebugLog.Write("[renderer] unsubscribed FrameArrived + TextureArrived");
    }

    private void AttachLocalSource(ICaptureSource source)
    {
        _attachedLocalSource = source;
        source.FrameArrived += OnFrameArrived;
        source.TextureArrived += OnTextureArrived;
        VicoScreenShare.Client.Diagnostics.DebugLog.Write("[renderer] subscribed to ICaptureSource.FrameArrived + TextureArrived (local preview)");
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
        VicoScreenShare.Client.Diagnostics.DebugLog.Write("[renderer] unsubscribed ICaptureSource.FrameArrived + TextureArrived (local preview)");
    }

    private long _frameArrivedCount;
    private long _frameFailureCount;

    private void OnFrameArrived(in CaptureFrameData frame)
    {
        if (frame.Format != CaptureFramePixelFormat.Bgra8)
        {
            return;
        }

        // Two routes from here:
        //
        //   - Remote stream path: copy the BGRA bytes into a
        //     DecodedVideoFrame, push onto the TimestampedFrameQueue.
        //     The PresentLoop pops on the content-timestamp schedule
        //     and calls PaintFromQueue.
        //   - Local self-preview path: paint inline on whatever thread
        //     the source fires on. Self-preview is a confirmation tile,
        //     not a smooth-playout feed, so the simpler inline path is
        //     the right shape.
        //
        // Wrapped in try/catch: a throw here would propagate through
        // StreamReceiver's FrameArrived?.Invoke and kill the RTP
        // receive thread.
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
                // The receiver path goes through the queue → present
                // loop. Copy the rented span into a DecodedVideoFrame so
                // the queue can hold onto it across threads.
                var bgraSize = frame.Height * frame.StrideBytes;
                var bytes = new byte[bgraSize];
                frame.Pixels.Slice(0, bgraSize).CopyTo(bytes);
                var decoded = new DecodedVideoFrame(bytes, frame.Width, frame.Height, frame.Timestamp);
                _playoutQueue.Push(in decoded);
            }
            else
            {
                // Self-preview path — still paint inline.
                OnFrameArrivedCore(in frame);
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
    }

    /// <summary>
    /// GPU texture fast path. Fires synchronously from the decoder thread
    /// when the source has a <see cref="IVideoDecoder.GpuOutputHandler"/>
    /// wired up and produces a BGRA <c>ID3D11Texture2D</c> on the shared
    /// device. We GPU-copy into our own <c>_uploadTexture</c> (so the
    /// decoder can safely reuse its texture on the next decode) and run
    /// the normal scale + present sequence inline. Skips the byte[] queue
    /// entirely — no CPU upload, no CPU→GPU PCIe transfer, which is what
    /// unlocks 4K120 on hardware decoders that were capped by the old
    /// readback path.
    ///
    /// The native texture pointer is valid only for the duration of this
    /// call; the <see cref="_context.CopyResource"/> below captures its
    /// contents into a locally-owned resource before we return.
    /// </summary>
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

                InputFrameCount++;

                InitD3DLocked();
                if (_swapChain is null || _device is null || _context is null)
                {
                    return;
                }

                var count = System.Threading.Interlocked.Increment(ref _frameArrivedCount);
                if (count <= 3 || count % 300 == 0)
                {
                    VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                        $"[renderer] gpu-frame #{count} {width}x{height} -> {_swapChainWidth}x{_swapChainHeight}");
                }

                // Receiver path: GPU-copy into an owned ring slot, then
                // push onto the playout queue. PaintFromQueue drains it
                // on the present thread, honoring ReceiveBufferFrames.
                // Local-preview path: paint inline (queue isn't used by
                // self-preview — it's a confirmation tile, not a paced
                // feed). _attachedReceiver is null for the preview path.
                if (_attachedReceiver is null)
                {
                    EnsureUploadTextureLocked(width, height);
                    using var previewSource = new ID3D11Texture2D(nativeTexture);
                    _context.CopyResource(_uploadTexture!, previewSource);
                    PaintUploadedTextureLocked(width, height);
                    return;
                }

                slot = AcquireRingSlotLocked();
                if (slot < 0)
                {
                    // Ring is saturated and SkipToLatest didn't free
                    // anything (pathological — paint thread is completely
                    // stalled). Drop this frame rather than block.
                    return;
                }

                EnsureRingSlotTextureLocked(slot, width, height);

                // Wrapping the caller's pointer AddRefs the underlying
                // COM object (Vortice's ComObject ctor). Disposing the
                // wrapper Releases. CopyResource queues a GPU-side DMA
                // from the decoder's BGRA texture into our owned slot
                // texture — no PCIe traffic, no CPU stall.
                using var sourceTexture = new ID3D11Texture2D(nativeTexture);
                _context.CopyResource(_textureRing![slot]!, sourceTexture);
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
            return;
        }

        // Push OUTSIDE the render lock: Push may synchronously fire
        // OnDropped if the queue is at capacity, which calls back into
        // ReleaseRingSlot which takes _renderLock. Keeping Push outside
        // avoids the re-entrancy hazard.
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
    /// PresentLoop paint callback. Runs on the present thread after the
    /// loop has slept to the next frame's due time. Shares the same
    /// D3D11 upload + scale + Present sequence as the inline self-preview
    /// path.
    /// </summary>
    private void PaintFromQueue(in DecodedVideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        // GPU path: pixel data lives in _textureRing[slot]; scale +
        // present directly. Release the slot after paint so the ring
        // pool can reuse it.
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
                    InitD3DLocked();
                    if (_swapChain is null || _device is null || _context is null)
                    {
                        return;
                    }
                    PaintSourceTextureLocked(_textureRing[slot]!, frame.Width, frame.Height);
                }
            }
            catch (Exception ex)
            {
                var count = System.Threading.Interlocked.Increment(ref _frameFailureCount);
                if (count <= 5 || count % 60 == 0)
                {
                    VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                        $"[renderer] PaintFromQueue (gpu) threw (#{count}): {ex.GetType().Name}: {ex.Message}");
                }
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

        // CPU path: wrap the decoded frame as a CaptureFrameData so we
        // can reuse OnFrameArrivedCore's upload + scale + present
        // sequence — that keeps the paint path identical between the
        // self-preview inline route and the receiver queue route.
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
            var count = System.Threading.Interlocked.Increment(ref _frameFailureCount);
            if (count <= 5 || count % 60 == 0)
            {
                VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                    $"[renderer] PaintFromQueue threw (#{count}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Scale the given source texture into the swap chain back buffer
    /// and Present. Same shape as <see cref="PaintUploadedTextureLocked"/>
    /// but takes any source texture — callers are either the CPU upload
    /// path (_uploadTexture) or the GPU ring path (_textureRing[slot]).
    /// </summary>
    private void PaintSourceTextureLocked(ID3D11Texture2D source, int width, int height)
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        EnsureScalerLocked(width, height, _swapChainWidth, _swapChainHeight);
        var destRect = ComputeLetterboxRect(width, height, _swapChainWidth, _swapChainHeight);
        try
        {
            _scaler!.Process(source, backBuffer, destRect);
        }
        catch (Exception ex)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write($"[renderer] scaler.Process threw: {ex.Message}");
            return;
        }
        try
        {
            _swapChain.Present(0, PresentFlags.None);
            PaintedFrameCount++;
        }
        catch (Exception ex)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write($"[renderer] Present threw: {ex.Message}");
        }
    }

    private void OnFrameArrivedCore(in CaptureFrameData frame)
    {
        var paintStart = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            // Lazy D3D init on first frame — we now know we have both
            // a valid hwnd AND WPF has had a chance to lay us out so
            // ActualWidth/Height are real numbers.
            InitD3DLocked();
            if (_swapChain is null || _device is null || _context is null)
            {
                return;
            }

            var width = frame.Width;
            var height = frame.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var count = System.Threading.Interlocked.Increment(ref _frameArrivedCount);
            if (count <= 3 || count % 300 == 0)
            {
                VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                    $"[renderer] frame #{count} {width}x{height} -> {_swapChainWidth}x{_swapChainHeight}");
            }

            EnsureUploadTextureLocked(width, height);

            unsafe
            {
                fixed (byte* src = frame.Pixels)
                {
                    _context.UpdateSubresource(
                        _uploadTexture!,
                        0,
                        null,
                        (IntPtr)src,
                        (uint)frame.StrideBytes,
                        (uint)(frame.StrideBytes * height));
                }
            }

            PaintUploadedTextureLocked(width, height);
        }
        var paintEnd = System.Diagnostics.Stopwatch.GetTimestamp();
        LastPaintMs = (paintEnd - paintStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>
    /// Scale the populated <see cref="_uploadTexture"/> into the current
    /// swap-chain back buffer and present. Shared by the CPU path (after
    /// <c>UpdateSubresource</c>) and the GPU path (after
    /// <c>CopyResource</c>). Caller must already hold <see cref="_renderLock"/>
    /// and have verified <c>_swapChain</c> / <c>_device</c> / <c>_context</c>
    /// are non-null.
    /// </summary>
    private void PaintUploadedTextureLocked(int width, int height)
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);

        EnsureScalerLocked(width, height, _swapChainWidth, _swapChainHeight);

        // Aspect-preserving fit: compute the letterbox rect inside the
        // back buffer that keeps the source's aspect ratio. The VP fills
        // the unused part with the background color we set on scaler
        // construction (black).
        var destRect = ComputeLetterboxRect(width, height, _swapChainWidth, _swapChainHeight);

        try
        {
            _scaler!.Process(_uploadTexture!, backBuffer, destRect);
        }
        catch (Exception ex)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write($"[renderer] scaler.Process threw: {ex.Message}");
            return;
        }

        // SyncInterval=0 ("uncapped"): Present returns as soon as the
        // back buffer is queued to DWM instead of blocking for the next
        // vsync. DWM itself still paces the compositor to the monitor
        // refresh rate, so you still cannot SEE more than the display
        // refresh — but the render thread stops being the bottleneck.
        // Without this, each frame cost ~6.9 ms of pure Present-block on
        // top of upload + scale, which capped paint to ~55 fps even when
        // the decoder was producing 120+.
        try
        {
            _swapChain.Present(0, PresentFlags.None);
            PaintedFrameCount++;
        }
        catch (Exception ex)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write($"[renderer] Present threw: {ex.Message}");
        }
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
            // Source is wider than the destination: fit width, letterbox
            // top and bottom.
            var h = (int)Math.Round(dstW / srcAspect);
            var y = (dstH - h) / 2;
            return new RawRect(0, y, dstW, y + h);
        }
        else
        {
            // Source is taller / same aspect: fit height, letterbox
            // left and right.
            var w = (int)Math.Round(dstH * srcAspect);
            var x = (dstW - w) / 2;
            return new RawRect(x, 0, x + w, dstH);
        }
    }

    /// <summary>
    /// Acquire a free GPU-ring slot for a new incoming texture frame.
    /// Lazy-initializes the ring arrays on first call. If every slot is
    /// busy (queue saturated — paint thread behind), forces the playout
    /// queue to drop its oldest entry, which fires OnDropped on that
    /// entry and releases its slot via <see cref="ReleaseRingSlotLocked"/>.
    /// Caller holds <see cref="_renderLock"/>. Returns -1 only in
    /// pathological cases where the eviction didn't yield a free slot.
    /// </summary>
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
        // entry; its OnDropped hook will release a slot back to us. Drop
        // the _renderLock first to avoid an ordering hazard — the queue's
        // OnDropped callback re-enters this renderer via ReleaseRingSlot
        // which takes _renderLock.
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

    /// <summary>
    /// Mark a ring slot free so future <see cref="AcquireRingSlotLocked"/>
    /// calls can reuse it. Safe to call from any thread — takes
    /// <see cref="_renderLock"/> internally because the playout queue
    /// invokes this outside the render lock via the frame's OnDropped
    /// hook. The slot's underlying texture stays allocated for reuse.
    /// </summary>
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

    /// <summary>
    /// Ensure the ring slot's texture is allocated at the requested
    /// dimensions. Reuses the existing texture when size matches —
    /// dimensions are stable across a stream so this is the common path
    /// and stays allocation-free after the first frame per slot.
    /// </summary>
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

    private void EnsureUploadTextureLocked(int width, int height)
    {
        if (_uploadTexture is not null && _uploadWidth == width && _uploadHeight == height)
        {
            return;
        }

        _uploadTexture?.Dispose();
        // DEFAULT usage + ShaderResource + RenderTarget. D3D11 Video
        // Processor rejects its input with E_INVALIDARG unless the source
        // texture has RenderTarget bind — even though we never actually
        // use the view, the runtime validates the bind-flag combination
        // before creating the input view. A DYNAMIC texture can't have
        // RenderTarget (D3D11 disallows that combo), so we use DEFAULT
        // and upload via UpdateSubresource instead of Map.
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

    private static void EnsureClassRegistered()
    {
        lock (ClassRegLock)
        {
            if (_classRegistered)
            {
                return;
            }
            // Shared WndProc that dispatches mouse messages back to the
            // HwndHost instance owning each HWND, so WPF sees clicks on
            // the video. Keep rooted so GC can't collect the delegate.
            WindowProcDelegate wndProc = ClassWndProc;
            _wndProcHandle = GCHandle.Alloc(wndProc);
            // CS_DBLCLKS (0x0008) enables WM_LBUTTONDBLCLK delivery. Without
            // it, Windows never promotes a fast LBUTTONDOWN pair to the
            // double-click message and double-click-to-fullscreen silently
            // falls back to two single clicks.
            const uint CS_DBLCLKS = 0x0008;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_DBLCLKS,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = GetModuleHandleW(null),
                // Hand cursor class-wide — the WPF Cursor="Hand" on the
                // tile Border can't reach the child HWND's area (HwndHost
                // airspace eats the cursor), so we bake it into the window
                // class here. Every renderer HWND gets the same hand.
                hCursor = LoadCursorW(IntPtr.Zero, IDC_HAND),
                // Black background brush so the child window is filled
                // with black on WM_ERASEBKGND before our first Present.
                // Without this, whatever was painted underneath the
                // HwndHost region (the previous page's UI) leaks through
                // until the first frame arrives — the "main menu burned
                // into the rendering area" bug.
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszClassName = WindowClassName,
            };
            RegisterClassExW(ref wc);
            _classRegistered = true;
        }
    }

    private static IntPtr ClassWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_instancesByHwnd.TryGetValue(hwnd, out var self))
        {
            var handled = self.DispatchMouseMessage(msg, lParam);
            if (handled)
            {
                return IntPtr.Zero;
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Forwards WM_LBUTTON* / WM_MOUSEMOVE into WPF via CLR events and
    /// command invocations. The child HWND would otherwise eat every
    /// click/drag in its area, leaving the video un-interactable.
    /// </summary>
    private bool DispatchMouseMessage(uint msg, IntPtr lParam)
    {
        // LOWORD/HIWORD of lParam are x/y in window client pixels; convert
        // to DIPs so event payloads match the WPF coordinate system.
        short px = (short)((int)lParam & 0xFFFF);
        short py = (short)(((int)lParam >> 16) & 0xFFFF);
        var scale = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            scale = source.CompositionTarget.TransformToDevice.M11;
        }
        var pos = new System.Windows.Point(px / scale, py / scale);

        switch (msg)
        {
            case WM_LBUTTONDOWN:
                _lbuttonDownInside = true;
                VideoMouseDown?.Invoke(this, new VideoMouseEventArgs(pos));
                return true;
            case WM_MOUSEMOVE:
                VideoMouseMove?.Invoke(this, new VideoMouseEventArgs(pos));
                return true;
            case WM_LBUTTONUP:
                VideoMouseUp?.Invoke(this, new VideoMouseEventArgs(pos));
                if (_lbuttonDownInside)
                {
                    _lbuttonDownInside = false;
                    var cmd = LeftClickCommand;
                    var param = LeftClickCommandParameter;
                    if (cmd is not null && cmd.CanExecute(param))
                    {
                        cmd.Execute(param);
                    }
                }
                return true;
            case WM_LBUTTONDBLCLK:
                VideoMouseDoubleClick?.Invoke(this, new VideoMouseEventArgs(pos));
                {
                    var cmd = LeftDoubleClickCommand;
                    var param = LeftDoubleClickCommandParameter;
                    if (cmd is not null && cmd.CanExecute(param))
                    {
                        cmd.Execute(param);
                    }
                }
                return true;
        }
        return false;
    }

    private delegate IntPtr WindowProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// Event payload for mouse events forwarded out of the child HWND into WPF.
/// Position is in the renderer's local DIP coordinate space.
/// </summary>
public sealed class VideoMouseEventArgs : EventArgs
{
    public VideoMouseEventArgs(System.Windows.Point position) { Position = position; }
    public System.Windows.Point Position { get; }
}
