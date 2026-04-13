using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Windows.Direct3D;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScreenSharing.Desktop.Wpf.Rendering;

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
    public static readonly DependencyProperty ReceiverProperty =
        DependencyProperty.Register(
            nameof(Receiver),
            typeof(StreamReceiver),
            typeof(D3DImageVideoRenderer),
            new PropertyMetadata(null, OnReceiverChanged));

    public StreamReceiver? Receiver
    {
        get => (StreamReceiver?)GetValue(ReceiverProperty);
        set => SetValue(ReceiverProperty, value);
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    private IntPtr _hwnd;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _uploadTexture;
    private D3D11VideoScaler? _scaler;
    private int _uploadWidth;
    private int _uploadHeight;
    private int _swapChainWidth;
    private int _swapChainHeight;
    private StreamReceiver? _attachedReceiver;
    private readonly object _renderLock = new();
    private bool _disposed;

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
            ScreenSharing.Client.Diagnostics.DebugLog.Write("[renderer] CreateWindowEx failed for video host");
            throw new InvalidOperationException("CreateWindowEx failed for video host");
        }

        ScreenSharing.Client.Diagnostics.DebugLog.Write($"[renderer] BuildWindowCore hwnd=0x{_hwnd.ToInt64():X}");

        // Do NOT create the swap chain here — ActualWidth/Height are 0
        // before WPF has laid the element out. InitD3D runs lazily on
        // the first OnFrameArrived call, when we know both the window
        // size (via Arrange) and the frame size.
        AttachReceiver(Receiver);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        lock (_renderLock)
        {
            _disposed = true;
            DetachReceiver();
            _scaler?.Dispose(); _scaler = null;
            _uploadTexture?.Dispose(); _uploadTexture = null;
            _swapChain?.Dispose(); _swapChain = null;
            _context?.Dispose(); _context = null;
            _device?.Dispose(); _device = null;
        }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private void InitD3DLocked()
    {
        if (_swapChain is not null) return;

        var sharedDevices = App.SharedDevices;
        if (sharedDevices is null)
        {
            ScreenSharing.Client.Diagnostics.DebugLog.Write("[renderer] App.SharedDevices is null — cannot init D3D");
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
            ScreenSharing.Client.Diagnostics.DebugLog.Write($"[renderer] swap chain created {_swapChainWidth}x{_swapChainHeight}");
        }
        catch (Exception ex)
        {
            ScreenSharing.Client.Diagnostics.DebugLog.Write($"[renderer] CreateSwapChainForHwnd threw: {ex.Message}");
            _device = null;
            _context = null;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        lock (_renderLock)
        {
            if (_disposed || _swapChain is null) return;
            var newW = Math.Max(1, (int)ActualWidth);
            var newH = Math.Max(1, (int)ActualHeight);
            if (newW == _swapChainWidth && newH == _swapChainHeight) return;

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
                _swapChainWidth = newW;
                _swapChainHeight = newH;
            }
            catch (Exception ex)
            {
                ScreenSharing.Client.Diagnostics.DebugLog.Write(
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
            self.DetachReceiver();
            self.AttachReceiver((StreamReceiver?)e.NewValue);
        }
    }

    private void AttachReceiver(StreamReceiver? receiver)
    {
        if (receiver is null)
        {
            ScreenSharing.Client.Diagnostics.DebugLog.Write("[renderer] AttachReceiver called with null receiver");
            return;
        }
        if (_hwnd == IntPtr.Zero)
        {
            // HwndHost hasn't built yet. BuildWindowCore will call
            // AttachReceiver(Receiver) once it has, so the subscription
            // lands at that point with the DP value already set.
            ScreenSharing.Client.Diagnostics.DebugLog.Write("[renderer] AttachReceiver skipped — hwnd not yet built");
            return;
        }
        _attachedReceiver = receiver;
        receiver.FrameArrived += OnFrameArrived;
        ScreenSharing.Client.Diagnostics.DebugLog.Write("[renderer] subscribed to StreamReceiver.FrameArrived");
    }

    private void DetachReceiver()
    {
        if (_attachedReceiver is null) return;
        try { _attachedReceiver.FrameArrived -= OnFrameArrived; } catch { }
        _attachedReceiver = null;
        ScreenSharing.Client.Diagnostics.DebugLog.Write("[renderer] unsubscribed StreamReceiver.FrameArrived");
    }

    private long _frameArrivedCount;
    private long _frameFailureCount;

    private void OnFrameArrived(in CaptureFrameData frame)
    {
        if (frame.Format != CaptureFramePixelFormat.Bgra8) return;

        // Everything below must be inside a try/catch. If we throw, the
        // exception propagates through StreamReceiver's FrameArrived?.Invoke
        // and takes down the RTP receive thread — the symptom is "first
        // frame renders, then 2s later the VM says Stream stalled" because
        // no subsequent frames make it past the multicast.
        try
        {
            OnFrameArrivedCore(in frame);
        }
        catch (Exception ex)
        {
            var count = System.Threading.Interlocked.Increment(ref _frameFailureCount);
            if (count <= 5 || count % 60 == 0)
            {
                ScreenSharing.Client.Diagnostics.DebugLog.Write(
                    $"[renderer] OnFrameArrived threw (#{count}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void OnFrameArrivedCore(in CaptureFrameData frame)
    {
        var paintStart = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_renderLock)
        {
            if (_disposed) return;

            InputFrameCount++;

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
            if (width <= 0 || height <= 0) return;

            var count = System.Threading.Interlocked.Increment(ref _frameArrivedCount);
            if (count <= 3 || count % 300 == 0)
            {
                ScreenSharing.Client.Diagnostics.DebugLog.Write(
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

            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);

            EnsureScalerLocked(width, height, _swapChainWidth, _swapChainHeight);

            // Aspect-preserving fit: compute the letterbox rect inside
            // the back buffer that keeps the source's aspect ratio. The
            // VP fills the unused part with the background color we set
            // on scaler construction (black).
            var destRect = ComputeLetterboxRect(width, height, _swapChainWidth, _swapChainHeight);

            try
            {
                _scaler!.Process(_uploadTexture!, backBuffer, destRect);
            }
            catch (Exception ex)
            {
                ScreenSharing.Client.Diagnostics.DebugLog.Write($"[renderer] scaler.Process threw: {ex.Message}");
                return;
            }

            // SyncInterval=1 locks Present to display vsync (=display
            // refresh rate). DWM hands the composed result to the
            // compositor at its own tick. No UI-thread work at all.
            try
            {
                // SyncInterval=0 ("uncapped"): Present returns as soon as
                // the back buffer is queued to DWM instead of blocking
                // for the next vsync. DWM itself still paces the
                // compositor to the monitor refresh rate, so you still
                // cannot SEE more than 144 fps on a 144 Hz display — but
                // the render thread stops being the bottleneck. Without
                // this, each frame cost ~6.9 ms of pure Present-block on
                // top of upload + scale, which capped paint to ~55 fps
                // even when the decoder was producing 120+.
                _swapChain.Present(0, PresentFlags.None);
                PaintedFrameCount++;
            }
            catch (Exception ex)
            {
                ScreenSharing.Client.Diagnostics.DebugLog.Write($"[renderer] Present threw: {ex.Message}");
            }
        }
        var paintEnd = System.Diagnostics.Stopwatch.GetTimestamp();
        LastPaintMs = (paintEnd - paintStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
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

    private void EnsureUploadTextureLocked(int width, int height)
    {
        if (_uploadTexture is not null && _uploadWidth == width && _uploadHeight == height) return;

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
            if (_classRegistered) return;
            // Keep the wndproc delegate rooted so the GC can't collect
            // it while Windows holds the function pointer.
            WindowProcDelegate wndProc = DefWindowProcW;
            _wndProcHandle = GCHandle.Alloc(wndProc);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = GetModuleHandleW(null),
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

    private delegate IntPtr WindowProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
