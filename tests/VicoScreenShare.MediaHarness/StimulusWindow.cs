using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VicoScreenShare.MediaHarness;

/// <summary>
/// A topmost popup window backed by a D3D11 swap chain, driven by a
/// background thread that calls <c>Present</c> at a fixed rate. Used as a
/// capture stimulus for <c>bench-capture-e2e</c>: WGC only produces a new
/// frame when the source actually changes, so a static desktop yields
/// ~2 fps. By capturing this window instead, we know exactly how often the
/// source is presenting a new frame and can measure whether the capture
/// pipeline keeps up.
/// </summary>
internal sealed class StimulusWindow : IDisposable
{
    private const string ClassName = "ScreenSharingStimulus";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WM_DESTROY = 0x0002;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential)]
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
        [MarshalAs(UnmanagedType.LPStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern ushort RegisterClassExA(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr CreateWindowExA(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcA(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleA(string? lpModuleName);

    private readonly int _width;
    private readonly int _height;
    private readonly int _targetFps;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly WndProc _wndProcDelegate;

    private IntPtr _hwnd;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;

    public StimulusWindow(int width, int height, int fps)
    {
        _width = width;
        _height = height;
        _targetFps = fps;
        _wndProcDelegate = DefWindowProcA;

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "StimulusWindow",
        };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("StimulusWindow failed to start within 5s");
        }
    }

    public IntPtr Hwnd => _hwnd;

    private void ThreadMain()
    {
        try
        {
            CreateWindow();
            CreateSwapChain();
            _ready.Set();
            RunPresentLoop();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"StimulusWindow thread threw: {ex.Message}");
            _ready.Set();
        }
        finally
        {
            _rtv?.Dispose();
            _swapChain?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        }
    }

    private void CreateWindow()
    {
        var hInstance = GetModuleHandleA(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        RegisterClassExA(ref wc); // OK to fail (already registered)

        _hwnd = CreateWindowExA(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            ClassName,
            "ScreenSharing stimulus",
            WS_POPUP | WS_VISIBLE,
            0, 0, _width, _height,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowEx failed");
        }
        ShowWindow(_hwnd, SW_SHOW);
        BringWindowToTop(_hwnd);
        SetForegroundWindow(_hwnd);
        // SWP_NOACTIVATE=0x10, SWP_SHOWWINDOW=0x40. Placing HWND_TOPMOST
        // (0xFFFFFFFF as -1) keeps it always-on-top so DWM keeps composing
        // it at the monitor refresh rate rather than the stepped-down
        // background-window cadence Windows 11 applies (~48 Hz on a
        // 144 Hz display).
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, _width, _height, 0x0040);
    }

    private void CreateSwapChain()
    {
        var hr = D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out _device,
            out _,
            out _context);
        hr.CheckError();

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Unspecified,
        };

        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer);
    }

    public long PresentCount => Interlocked.Read(ref _presentCount);

    private long _presentCount;

    private void RunPresentLoop()
    {
        // Clear the back buffer to a color that changes each frame so WGC
        // sees a genuinely-new frame every iteration (static content gets
        // deduped).
        var frameGapMs = 1000.0 / _targetFps;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long frame = 0;
        var nextDeadlineMs = sw.Elapsed.TotalMilliseconds;
        var lastReportMs = 0.0;
        long lastReportedPresents = 0;
        while (!_cts.IsCancellationRequested)
        {
            var r = (float)((Math.Sin(frame * 0.07) + 1) * 0.5);
            var g = (float)((Math.Sin(frame * 0.11 + 1) + 1) * 0.5);
            var b = (float)((Math.Sin(frame * 0.13 + 2) + 1) * 0.5);
            _context!.ClearRenderTargetView(_rtv!, new Color4(r, g, b, 1.0f));
            _swapChain!.Present(0, PresentFlags.None);
            Interlocked.Increment(ref _presentCount);
            frame++;

            var nowMs = sw.Elapsed.TotalMilliseconds;
            if (nowMs - lastReportMs > 1000)
            {
                var presentsThisSec = _presentCount - lastReportedPresents;
                Console.WriteLine($"# stimulus presents/sec={presentsThisSec}");
                lastReportedPresents = _presentCount;
                lastReportMs = nowMs;
            }

            nextDeadlineMs += frameGapMs;
            var waitMs = nextDeadlineMs - nowMs;
            if (waitMs > 1)
            {
                Thread.Sleep((int)waitMs);
            }
            else if (waitMs < -frameGapMs * 2)
            {
                nextDeadlineMs = nowMs;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
