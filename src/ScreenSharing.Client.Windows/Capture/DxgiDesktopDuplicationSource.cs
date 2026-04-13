using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Windows.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenSharing.Client.Windows.Capture;

/// <summary>
/// <see cref="ICaptureSource"/> backed by the DXGI Desktop Duplication API.
///
/// Reads directly from the GPU scanout path and bypasses DWM compose
/// throttling. WGC (<see cref="WindowsCaptureSource"/>) is subject to DWM's
/// "this window looks idle" heuristic and drops to 30–45 fps when the user
/// cursor sits still over the captured window — DDA isn't, so this is the
/// backend for "Share Screen" where the use case is high-rate gaming
/// capture. Monitor-scoped: cannot capture a single window.
/// </summary>
public sealed class DxgiDesktopDuplicationSource : ICaptureSource
{
    // AcquireNextFrame blocks the caller until a new frame arrives or the
    // timeout fires. Keeping it small bounds the worst-case shutdown latency
    // (StopAsync has to wait for the current call to return before it can
    // dispose the duplication). 100 ms is small enough that teardown feels
    // instant while still letting the driver sleep between vblanks instead
    // of busy-looping on a truly static screen.
    private const int AcquireTimeoutMs = 100;

    // Error codes returned via SharpGen.Runtime.Result.Code — we compare
    // against the well-known DXGI values directly so we don't have to take
    // a compile-time dep on Vortice's ResultCode mapping (which has moved
    // between versions).
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);

    private const int PreviewReadbackFps = 30;
    private readonly long _previewReadbackGapTicks =
        TimeSpan.FromSeconds(1.0 / PreviewReadbackFps).Ticks;
    private long _lastPreviewReadbackTicks = long.MinValue;

    private readonly D3D11DeviceManager _devices;
    private readonly int _outputIndex;
    private readonly Stopwatch _timer = Stopwatch.StartNew();

    // All fields below are only touched by the pump thread once the thread
    // is running. StopAsync cancels and joins before disposing anything, so
    // no locking is needed for the hot path.
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _frameTexture;
    private ID3D11Texture2D? _stagingTexture;
    private int _frameWidth;
    private int _frameHeight;
    private byte[] _cpuBuffer = Array.Empty<byte>();

    private Thread? _pumpThread;
    private CancellationTokenSource? _pumpCts;
    private volatile bool _closed;
    private bool _disposed;

    public DxgiDesktopDuplicationSource(D3D11DeviceManager devices, int outputIndex, string displayName)
    {
        _devices = devices;
        _outputIndex = outputIndex;
        DisplayName = displayName;
    }

    public string DisplayName { get; }

    public event FrameArrivedHandler? FrameArrived;
    public event TextureArrivedHandler? TextureArrived;
    public event Action? Closed;

    public Task StartAsync()
    {
        if (_pumpThread is not null || _disposed || _closed)
        {
            return Task.CompletedTask;
        }

        InitializeDuplication();

        _pumpCts = new CancellationTokenSource();
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = $"DDA-Capture-Output{_outputIndex}",
            // AboveNormal so the acquire/copy pair hits every vblank even
            // under contention from the encoder pump + UI render threads.
            Priority = ThreadPriority.AboveNormal,
        };
        _pumpThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        var cts = _pumpCts;
        _pumpCts = null;
        cts?.Cancel();

        // Join first, then tear down COM resources on this thread. Because
        // the pump thread is the only one touching _duplication /
        // _frameTexture / _stagingTexture / _devices.Context, waiting for
        // it to exit is the synchronization — no lock required.
        var thread = _pumpThread;
        _pumpThread = null;
        thread?.Join(TimeSpan.FromMilliseconds(AcquireTimeoutMs + 2000));

        _duplication?.Dispose();
        _duplication = null;
        _frameTexture?.Dispose();
        _frameTexture = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _frameWidth = 0;
        _frameHeight = 0;

        cts?.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private void InitializeDuplication()
    {
        using var dxgiDevice = _devices.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();

        var enumResult = adapter.EnumOutputs((uint)_outputIndex, out var output);
        if (enumResult.Failure || output is null)
        {
            throw new InvalidOperationException(
                $"DDA: cannot enumerate output index {_outputIndex} (hr=0x{enumResult.Code:x8}).");
        }

        try
        {
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            // DuplicateOutput takes an IUnknown; ID3D11Device is the
            // common choice and binds the duplication to our shared
            // capture device so CopyResource stays in-device.
            _duplication = output1.DuplicateOutput(_devices.Device);

            var desc = _duplication.Description;
            EnsureFrameTexture((int)desc.ModeDescription.Width, (int)desc.ModeDescription.Height);
        }
        finally
        {
            output.Dispose();
        }
    }

    private void EnsureFrameTexture(int width, int height)
    {
        if (_frameTexture is not null && _frameWidth == width && _frameHeight == height) return;

        _frameTexture?.Dispose();
        _stagingTexture?.Dispose();
        _stagingTexture = null;

        // DEFAULT usage + ShaderResource|RenderTarget so the GPU scaler
        // inside MediaFoundationH264Encoder can sample it directly — no
        // CPU roundtrip on the fast texture path.
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.None,
        };
        _frameTexture = _devices.Device.CreateTexture2D(desc);
        _frameWidth = width;
        _frameHeight = height;
    }

    private void EnsureStagingTexture(int width, int height)
    {
        if (_stagingTexture is not null) return;

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            ArraySize = 1,
            MipLevels = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _stagingTexture = _devices.Device.CreateTexture2D(desc);
    }

    private void PumpLoop()
    {
        var ct = _pumpCts?.Token ?? CancellationToken.None;
        try
        {
            while (!ct.IsCancellationRequested && !_disposed && !_closed)
            {
                if (!AcquireAndProcessOnce())
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ScreenSharing.Client.Diagnostics.DebugLog.Write($"[dda] pump loop fatal: {ex.Message}");
        }
        finally
        {
            if (!_closed)
            {
                _closed = true;
                try { Closed?.Invoke(); } catch { }
            }
        }
    }

    private bool AcquireAndProcessOnce()
    {
        var duplication = _duplication;
        if (duplication is null) return false;

        var hr = duplication.AcquireNextFrame((uint)AcquireTimeoutMs, out _, out var resource);
        if (hr.Failure)
        {
            if (hr.Code == DxgiErrorWaitTimeout)
            {
                // No change on screen — loop. For gaming this practically
                // never fires because the game keeps presenting.
                return true;
            }
            if (hr.Code == DxgiErrorAccessLost)
            {
                // Fires on mode changes, UAC secure-desktop transitions,
                // fullscreen app launches, GPU driver resets. Rebuild the
                // duplication and keep running.
                ScreenSharing.Client.Diagnostics.DebugLog.Write(
                    "[dda] access lost — reinitializing duplication");
                try { _duplication?.Dispose(); } catch { }
                _duplication = null;
                try
                {
                    InitializeDuplication();
                    return true;
                }
                catch (Exception ex)
                {
                    ScreenSharing.Client.Diagnostics.DebugLog.Write(
                        $"[dda] duplication reinit failed: {ex.Message}");
                    return false;
                }
            }
            ScreenSharing.Client.Diagnostics.DebugLog.Write(
                $"[dda] AcquireNextFrame failed hr=0x{hr.Code:x8}");
            return false;
        }

        try
        {
            if (resource is null) return true;

            using var desktopTexture = resource.QueryInterface<ID3D11Texture2D>();
            var texDesc = desktopTexture.Description;
            EnsureFrameTexture((int)texDesc.Width, (int)texDesc.Height);
            _devices.Context.CopyResource(_frameTexture!, desktopTexture);

            DispatchFrame();
        }
        catch (Exception ex)
        {
            ScreenSharing.Client.Diagnostics.DebugLog.Write(
                $"[dda] frame dispatch threw: {ex.Message}");
        }
        finally
        {
            resource?.Dispose();
            try { duplication.ReleaseFrame(); } catch { }
        }
        return true;
    }

    private void DispatchFrame()
    {
        if (_frameTexture is null) return;
        var width = _frameWidth;
        var height = _frameHeight;
        var timestamp = _timer.Elapsed;

        // Fast path: hand the GPU texture to the hardware encoder via
        // TextureArrived. The handler wraps the pointer in its own COM
        // wrapper and Release-es on dispose — AddRef once here so that
        // Release doesn't drop the refcount on _frameTexture to zero and
        // break subsequent frames / CPU readback below.
        var textureHandler = TextureArrived;
        if (textureHandler is not null)
        {
            _frameTexture.AddRef();
            try
            {
                textureHandler(_frameTexture.NativePointer, width, height, timestamp);
            }
            catch (Exception ex)
            {
                ScreenSharing.Client.Diagnostics.DebugLog.Write(
                    $"[dda] TextureArrived handler threw: {ex.Message}");
            }
        }

        // CPU readback for the local preview renderer. Same throttle rules
        // as WindowsCaptureSource: skip entirely when nobody's subscribed,
        // otherwise rate-limit to PreviewReadbackFps so the readback
        // doesn't cap the encoder path.
        if (FrameArrived is null) return;

        var nowTicks = timestamp.Ticks;
        if (_lastPreviewReadbackTicks != long.MinValue &&
            nowTicks - _lastPreviewReadbackTicks < _previewReadbackGapTicks)
        {
            return;
        }
        _lastPreviewReadbackTicks = nowTicks;

        EnsureStagingTexture(width, height);
        _devices.Context.CopyResource(_stagingTexture!, _frameTexture);

        var mapped = _devices.Context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = width * 4;
            var required = height * rowBytes;
            if (_cpuBuffer.Length < required)
            {
                _cpuBuffer = new byte[required];
            }

            unsafe
            {
                var src = (byte*)mapped.DataPointer;
                fixed (byte* dst = _cpuBuffer)
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
                _cpuBuffer.AsSpan(0, required),
                width,
                height,
                rowBytes,
                CaptureFramePixelFormat.Bgra8,
                timestamp);
            FrameArrived?.Invoke(in data);
        }
        finally
        {
            _devices.Context.Unmap(_stagingTexture!, 0);
        }
    }
}
