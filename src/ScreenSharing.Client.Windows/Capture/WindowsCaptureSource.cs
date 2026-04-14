using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Windows.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using global::Windows.Graphics;
using global::Windows.Graphics.Capture;
using global::Windows.Graphics.DirectX;
using global::Windows.Graphics.DirectX.Direct3D11;

namespace ScreenSharing.Client.Windows.Capture;

/// <summary>
/// Windows.Graphics.Capture-backed implementation of <see cref="ICaptureSource"/>.
/// Owns a <see cref="Direct3D11CaptureFramePool"/> and a staging texture; each
/// incoming frame is copied to the staging texture, mapped to CPU, and handed to
/// subscribers as a <see cref="CaptureFrameData"/>. The byte buffer is pooled so
/// there is no per-frame allocation on the hot path.
/// </summary>
public sealed class WindowsCaptureSource : ICaptureSource
{
    private readonly GraphicsCaptureItem _item;
    private readonly D3D11DeviceManager _devices;
    private readonly int _targetFrameRate;
    private readonly Stopwatch _timer = Stopwatch.StartNew();

    // Preview CPU readback is throttled to this rate so the encoder (which
    // runs through TextureArrived) doesn't get capped to the preview rate.
    // 30 fps is more than enough for a self-view of what you're sharing;
    // raising it past 60 starts eating into the framepool's delivery budget
    // because the readback blocks the WGC callback thread.
    private const int PreviewReadbackFps = 30;
    private readonly long _previewReadbackGapTicks =
        TimeSpan.FromSeconds(1.0 / PreviewReadbackFps).Ticks;
    private long _lastPreviewReadbackTicks = long.MinValue;

    // Serializes the free-threaded framepool callback against StopAsync /
    // DisposeAsync. Without it, a frame callback can be running CopyResource +
    // Map on the D3D11 Context when the UI thread disposes the framepool and
    // staging texture out from under it, which corrupts the native heap and
    // surfaces as a stack-less ExecutionEngineException.
    private readonly object _frameLock = new();
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _stagingTexture;
    private int _stagingWidth;
    private int _stagingHeight;
    private byte[] _cpuBuffer = Array.Empty<byte>();
    private bool _closed;
    private bool _disposed;

    // === Sender pace ===
    // The capture rate (WGC fires) is irregular: bursts of frames followed
    // by gaps because DWM acquires surfaces on its own schedule. The
    // encoder needs to receive frames at a regular cadence so the receiver
    // can replay them on a regular cadence — irregular wire timestamps
    // bake jitter into the receiver's playback that no jitter buffer can
    // unwind.
    //
    // The fix: the WGC callback writes the freshest frame into a
    // persistent slot texture (CopyResource) and a separate pace thread
    // dispatches TextureArrived at exactly the configured target frame
    // rate. The pace thread's PTS is monotonic at 1/fps spacing — not
    // derived from any wall clock — so the receiver gets metronome-regular
    // RTP timestamps regardless of how WGC actually delivered the source
    // frames. Capture jitter dies in the slot.
    private readonly object _slotLock = new();
    private ID3D11Texture2D? _slotTexture;
    private int _slotWidth;
    private int _slotHeight;
    private bool _slotHasContent;
    private Thread? _paceThread;
    private CancellationTokenSource? _paceCts;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    /// <summary>
    /// Construct a WGC-backed capture source that emits frames at exactly
    /// <paramref name="targetFrameRate"/> Hz to subscribers via a slot +
    /// pace-thread architecture (see <see cref="PaceLoop"/>).
    /// </summary>
    public WindowsCaptureSource(GraphicsCaptureItem item, D3D11DeviceManager devices, int targetFrameRate)
    {
        _item = item;
        _devices = devices;
        _targetFrameRate = Math.Clamp(targetFrameRate, 1, 240);
        DisplayName = item.DisplayName;
        _item.Closed += OnItemClosed;
    }

    public string DisplayName { get; }

    public event FrameArrivedHandler? FrameArrived;

    public event TextureArrivedHandler? TextureArrived;

    public event Action? Closed;

    public Task StartAsync()
    {
        if (_session is not null || _closed || _disposed)
        {
            return Task.CompletedTask;
        }

        var size = _item.Size;

        // Pre-size the staging texture to the item's current size so the first
        // FrameArrived goes straight through the copy path instead of being
        // dropped on the size-change branch.
        RecreateStagingTexture(size.Width, size.Height);

        // Direct3D11CaptureFramePool.Create would require a WinRT DispatcherQueue
        // on the calling thread, which a plain Avalonia UI thread does not have;
        // in that mode only the first FrameArrived ever fires. CreateFreeThreaded
        // dispatches frames on a thread-pool thread instead, which is the right
        // choice for a non-XAML desktop app.
        // numberOfBuffers: more buffers = more headroom for callback jitter
        // before WGC has to stall or drop a frame. 2 (the docs' minimum
        // example) was capping us at ~48 fps even when the callback took
        // <5 ms, because a single long callback (preview readback at 4K,
        // encoder warmup, GC pause) is enough to fill both slots. 6 leaves
        // room for ~40 ms of jitter at 144 Hz.
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _devices.WinRTDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 6,
            size: size);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = true; } catch { }

        // Hide the yellow capture border on Windows 11+. Not just
        // cosmetic — the border adds a DWM pass on every frame which
        // bounds our capture rate.
        try
        {
            if (GraphicsCaptureSession.IsSupported() &&
                global::Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                _session.IsBorderRequired = false;
            }
        }
        catch (Exception ex)
        {
            ScreenSharing.Client.Diagnostics.DebugLog.Write($"[capture] IsBorderRequired set failed: {ex.Message}");
        }

        // MinUpdateInterval controls the capture rate ceiling. Default
        // behavior on Windows 11 paces captured windows at a fraction of
        // the display refresh rate (observed ~48 Hz on a 144 Hz display).
        // Setting it to zero unlocks full-rate delivery. Requires Win11
        // build 22621+ (GraphicsCaptureSession5 contract).
        try
        {
            if (global::Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
            {
                _session.MinUpdateInterval = TimeSpan.Zero;
                ScreenSharing.Client.Diagnostics.DebugLog.Write("[capture] MinUpdateInterval=0 (full-rate delivery)");
            }
            else
            {
                ScreenSharing.Client.Diagnostics.DebugLog.Write("[capture] MinUpdateInterval not available on this Windows build");
            }
        }
        catch (Exception ex)
        {
            ScreenSharing.Client.Diagnostics.DebugLog.Write($"[capture] MinUpdateInterval set failed: {ex.Message}");
        }

        _session.StartCapture();

        // Start the sender pace thread. It blocks until the slot has its
        // first frame, then dispatches TextureArrived on a 1/_targetFrameRate
        // cadence with monotonic pace PTSes.
        _paceCts = new CancellationTokenSource();
        _paceThread = new Thread(PaceLoop)
        {
            IsBackground = true,
            Name = $"WGC-Pace-{_targetFrameRate}fps",
            // AboveNormal so the encoder dispatch hits its tick deadline
            // even under GC / encoder thread contention.
            Priority = ThreadPriority.AboveNormal,
        };
        _paceThread.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // Unsubscribe outside the lock so we don't deadlock against a callback
        // that's already waiting to acquire _frameLock. C# event removal is
        // atomic enough that no new callback will be dispatched once this
        // returns; the lock handles the already-in-flight one.
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        // Stop the pace thread BEFORE disposing the framepool / slot so
        // the dispatch loop doesn't try to AddRef a slot texture that
        // we're about to free.
        _paceCts?.Cancel();
        var paceThread = _paceThread;
        _paceThread = null;
        try { paceThread?.Join(TimeSpan.FromSeconds(2)); } catch { }
        _paceCts?.Dispose();
        _paceCts = null;

        lock (_frameLock)
        {
            _session?.Dispose();
            _session = null;
            _framePool?.Dispose();
            _framePool = null;
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        lock (_frameLock)
        {
            if (_disposed) return;
            _disposed = true;
            _stagingTexture?.Dispose();
            _stagingTexture = null;
        }
        lock (_slotLock)
        {
            _slotTexture?.Dispose();
            _slotTexture = null;
            _slotHasContent = false;
        }
        _item.Closed -= OnItemClosed;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _closed = true;
        Closed?.Invoke();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // The entire callback runs under _frameLock so a concurrent teardown
        // cannot dispose the framepool, staging texture, or D3D context while
        // we're mid-copy. The lock body is a handful of GPU ops + a memcpy, so
        // the contention it adds to shutdown is negligible (sub-millisecond).
        lock (_frameLock)
        {
            if (_disposed || _framePool is null) return;

            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            ProcessFrameLocked(sender, frame);
        }
    }

    private void ProcessFrameLocked(Direct3D11CaptureFramePool sender, Direct3D11CaptureFrame frame)
    {
        var width = frame.ContentSize.Width;
        var height = frame.ContentSize.Height;
        if (width <= 0 || height <= 0) return;

        // On content resize: rebuild both the staging texture AND the framepool.
        // Per Microsoft's Windows Graphics Capture guidance, the framepool must
        // be Recreate'd when the frame buffer size changes — skipping it keeps
        // the pool clipped to its original size so larger content gets cropped
        // and smaller content leaves undefined data. We still process the current
        // frame: the IDirect3DSurface we already retrieved holds its own COM ref
        // so Recreate-ing the pool does not invalidate it.
        if (width != _stagingWidth || height != _stagingHeight)
        {
            RecreateStagingTexture(width, height);
            try
            {
                sender.Recreate(
                    _devices.WinRTDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    numberOfBuffers: 6,
                    size: new SizeInt32 { Width = width, Height = height });
            }
            catch (ObjectDisposedException) { /* raced with Dispose */ }
        }

        var texPtr = Direct3D11Interop.GetD3D11Texture2DFromSurface(frame.Surface);
        if (texPtr == IntPtr.Zero) return;
        using var sourceTexture = new ID3D11Texture2D(texPtr);

        var timestamp = _timer.Elapsed;

        // Update the persistent slot texture with the freshest captured
        // frame. The pace thread reads this slot at exactly 1/_targetFrameRate
        // and dispatches TextureArrived to the encoder, so the WGC
        // burstiness never reaches the wire.
        //
        // The lock prevents the pace thread from reading the slot mid-copy
        // (the AddRef+dispatch on the pace thread is also under this
        // lock). D3D11 multithread protection serializes the underlying
        // immediate-context calls, so a concurrent encoder Process+
        // CopyResource is safe at the GPU level — the lock is purely to
        // protect the slot texture *reference* from being torn down while
        // the pace thread is using it.
        lock (_slotLock)
        {
            if (_slotTexture is null || _slotWidth != width || _slotHeight != height)
            {
                RecreateSlotTextureLocked(width, height);
            }
            if (_slotTexture is not null)
            {
                _devices.Context.CopyResource(_slotTexture, sourceTexture);
                _slotHasContent = true;
            }
        }

        // CPU readback for the local preview renderer. Two throttles:
        //   1. Skip entirely when there's no CPU subscriber (encoder on
        //      the texture path, no preview wired) — 5–8 ms per frame of
        //      GPU→staging→Map→memcpy disappears.
        //   2. When there IS a preview subscriber, rate-limit the readback
        //      to PreviewReadbackFps so we don't cap the framepool's
        //      delivery rate to the preview rate. The texture path fired
        //      above is not affected and keeps running at full rate.
        if (FrameArrived is null)
        {
            return;
        }

        var nowTicks = timestamp.Ticks;
        if (_lastPreviewReadbackTicks != long.MinValue &&
            nowTicks - _lastPreviewReadbackTicks < _previewReadbackGapTicks)
        {
            return;
        }
        _lastPreviewReadbackTicks = nowTicks;

        _devices.Context.CopyResource(_stagingTexture!, sourceTexture);

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

    private void RecreateStagingTexture(int width, int height)
    {
        _stagingTexture?.Dispose();
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
        _stagingWidth = width;
        _stagingHeight = height;
    }

    /// <summary>
    /// Allocate (or grow) the persistent slot texture that mirrors the
    /// most recent captured frame. Default usage + ShaderResource bind so
    /// the encoder's GPU pipeline can sample it directly. Caller must
    /// hold <see cref="_slotLock"/>.
    /// </summary>
    private void RecreateSlotTextureLocked(int width, int height)
    {
        _slotTexture?.Dispose();
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
        _slotTexture = _devices.Device.CreateTexture2D(desc);
        _slotWidth = width;
        _slotHeight = height;
        _slotHasContent = false;
    }

    /// <summary>
    /// Sender pace thread. Ticks at exactly <c>1/_targetFrameRate</c>
    /// seconds, reads whatever is in the slot, and dispatches
    /// <see cref="TextureArrived"/> with a monotonic pace PTS
    /// (<c>tickIndex * intervalSeconds</c>). Burstiness on the WGC side
    /// never reaches subscribers — only this thread's metronome does.
    /// </summary>
    private void PaceLoop()
    {
        // Drop the system timer slice from ~15.6 ms to 1 ms so
        // Thread.Sleep / spin-wait can hit sub-frame deadlines.
        TimeBeginPeriod(1);
        try
        {
            var ct = _paceCts?.Token ?? CancellationToken.None;
            var sw = Stopwatch.StartNew();
            var ticksPerSecond = (double)Stopwatch.Frequency;
            var ticksPerMs = ticksPerSecond / 1000.0;
            var intervalSeconds = 1.0 / _targetFrameRate;
            var intervalStopwatchTicks = (long)(intervalSeconds * ticksPerSecond);
            var ptsTicksPerInterval = TimeSpan.TicksPerSecond / (double)_targetFrameRate;

            long tickIndex = 0;
            long deadline = sw.ElapsedTicks;

            // Wait for the first WGC frame so we have something to
            // dispatch. Without this, the very first tick would hit
            // an empty slot and silently skip, the second tick would
            // be a full interval later, and the receiver would see a
            // 1-interval startup gap before any frames arrived.
            while (!ct.IsCancellationRequested)
            {
                bool ready;
                lock (_slotLock) ready = _slotHasContent;
                if (ready) break;
                Thread.Sleep(1);
            }

            while (!ct.IsCancellationRequested)
            {
                // Sleep + spin-tail to the next tick deadline.
                while (true)
                {
                    long remaining = deadline - sw.ElapsedTicks;
                    if (remaining <= 0) break;
                    var remainingMs = remaining / ticksPerMs;
                    if (remainingMs > 2.0)
                    {
                        Thread.Sleep((int)(remainingMs - 1));
                    }
                    else
                    {
                        while (sw.ElapsedTicks < deadline) Thread.SpinWait(64);
                        break;
                    }
                }
                if (ct.IsCancellationRequested) break;

                // Compute the pace PTS for this tick. tick 0 → 0,
                // tick N → N × interval. Monotonic, exact.
                var pts = TimeSpan.FromTicks((long)(tickIndex * ptsTicksPerInterval));
                tickIndex++;

                // Snapshot slot reference under the lock and dispatch
                // OUTSIDE the lock so a slow encoder doesn't block a
                // concurrent WGC fire from updating the slot for a
                // future tick. The AddRef keeps the slot texture alive
                // for the duration of the dispatch even if RecreateSlot
                // fires on another thread (which would Dispose the
                // managed wrapper but the COM object stays alive until
                // the handler's Release).
                ID3D11Texture2D? snapshot = null;
                int snapW = 0, snapH = 0;
                lock (_slotLock)
                {
                    if (_slotTexture is not null && _slotHasContent)
                    {
                        snapshot = _slotTexture;
                        snapW = _slotWidth;
                        snapH = _slotHeight;
                        snapshot.AddRef();
                    }
                }

                if (snapshot is not null)
                {
                    var handler = TextureArrived;
                    if (handler is not null)
                    {
                        try
                        {
                            handler(snapshot.NativePointer, snapW, snapH, pts);
                        }
                        catch (Exception ex)
                        {
                            ScreenSharing.Client.Diagnostics.DebugLog.Write(
                                $"[pace] TextureArrived handler threw: {ex.Message}");
                            // Handler didn't take ownership — release
                            // the AddRef ourselves so we don't leak.
                            snapshot.Release();
                        }
                    }
                    else
                    {
                        snapshot.Release();
                    }
                }

                deadline += intervalStopwatchTicks;
                // If we're more than one whole interval past the
                // intended deadline (long encoder stall, GC pause),
                // reanchor instead of trying to catch up — catch-up
                // would burst paints back-to-back which the receiver
                // would interpret as a sudden frame rate spike.
                long nowTicks = sw.ElapsedTicks;
                if (nowTicks - deadline > intervalStopwatchTicks)
                {
                    deadline = nowTicks + intervalStopwatchTicks;
                }
            }
        }
        catch (Exception ex)
        {
            ScreenSharing.Client.Diagnostics.DebugLog.Write($"[pace] loop fatal: {ex.Message}");
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }
}
