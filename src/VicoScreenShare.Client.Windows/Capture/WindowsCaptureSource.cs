namespace VicoScreenShare.Client.Windows.Capture;

using System;
using System.Threading;
using System.Threading.Tasks;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Windows.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using global::Windows.Graphics;
using global::Windows.Graphics.Capture;
using global::Windows.Graphics.DirectX;

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

    // Event-driven rate admission. Every WGC frame goes through the
    // pacer; admitted frames are dispatched inline (TextureArrived on
    // the callback thread) carrying frame.SystemRelativeTime verbatim.
    private readonly FrameRatePacer _pacer;

    // Persistent snapshot texture. Every admitted WGC frame is
    // CopyResource'd into this texture BEFORE firing TextureArrived.
    // Without the copy, the handler's downstream GPU work (encoder
    // scaler blit, readback) would race with the WGC framepool
    // recycling the surface for the next capture — DWM overwrites the
    // surface the moment we dispose the frame, and any pending GPU
    // reads see the NEXT capture's content instead of this one.
    // CopyResource on the immediate context is queue-ordered: it
    // completes (in the GPU command stream) before any subsequent
    // command the handler queues, so the handler always reads a stable
    // snapshot.
    private readonly object _snapshotLock = new();
    private ID3D11Texture2D? _snapshotTexture;
    private int _snapshotWidth;
    private int _snapshotHeight;

    // Diagnostic counters the capture-test stats panel reads.
    private long _wgcFrameCount;
    private long _dispatchedFrameCount;

    /// <summary>Total WGC framepool callbacks since construction. The
    /// delta between two reads gives the raw WGC arrival rate before the
    /// pacer drops the stream to <c>targetFrameRate</c>.</summary>
    public long WgcFrameCount => Interlocked.Read(ref _wgcFrameCount);

    /// <summary>Total admitted frames dispatched to subscribers. In
    /// steady state this approaches <c>targetFrameRate</c> × elapsed
    /// seconds.</summary>
    public long DispatchedFrameCount => Interlocked.Read(ref _dispatchedFrameCount);

    /// <summary>
    /// Construct a WGC-backed capture source. Each WGC frame is admitted
    /// by a <see cref="FrameRatePacer"/> against the configured
    /// <paramref name="targetFrameRate"/>, and admitted frames fire
    /// <see cref="TextureArrived"/> / <see cref="FrameArrived"/> inline
    /// on the WGC callback thread. The timestamp propagated downstream
    /// is <see cref="Direct3D11CaptureFrame.SystemRelativeTime"/> from
    /// WGC — the capture clock, verbatim.
    /// </summary>
    public WindowsCaptureSource(GraphicsCaptureItem item, D3D11DeviceManager devices, int targetFrameRate)
    {
        _item = item;
        _devices = devices;
        _targetFrameRate = Math.Clamp(targetFrameRate, 1, 240);
        _pacer = new FrameRatePacer(_targetFrameRate);
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
            VicoScreenShare.Client.Diagnostics.DebugLog.Write($"[capture] IsBorderRequired set failed: {ex.Message}");
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
                VicoScreenShare.Client.Diagnostics.DebugLog.Write("[capture] MinUpdateInterval=0 (full-rate delivery)");
            }
            else
            {
                VicoScreenShare.Client.Diagnostics.DebugLog.Write("[capture] MinUpdateInterval not available on this Windows build");
            }
        }
        catch (Exception ex)
        {
            VicoScreenShare.Client.Diagnostics.DebugLog.Write($"[capture] MinUpdateInterval set failed: {ex.Message}");
        }

        _session.StartCapture();
        _pacer.Reset();
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

        lock (_frameLock)
        {
            _session?.Dispose();
            _session = null;
            _framePool?.Dispose();
            _framePool = null;
        }
        _pacer.Reset();
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
            _snapshotTexture?.Dispose();
            _snapshotTexture = null;
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

        Interlocked.Increment(ref _wgcFrameCount);

        // Content timestamp: use frame.SystemRelativeTime directly.
        // That's DWM's composition time for the captured surface — the
        // capture clock — and we propagate it end-to-end through the
        // encoder SampleTime, decoder SampleTime, and the receiver
        // playout loop.
        var contentTimestamp = frame.SystemRelativeTime;

        // Rate admission. The pacer holds a running accepted count and
        // admits each frame only if (ts - firstTs) >= count * (1/fps).
        // Source slower than target → pass-through. Source faster than
        // target → caps to target fps. First frame always admits.
        if (!_pacer.ShouldAccept(contentTimestamp))
        {
            return;
        }

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

        // Snapshot the WGC surface into a persistent texture BEFORE
        // firing TextureArrived. The WGC framepool recycles the surface
        // the moment we dispose `frame` at the end of this callback —
        // DWM overwrites it for the next capture. Any GPU work the
        // handler queues (encoder scaler blit, readback CopyResource)
        // would race with that overwrite and read the NEXT capture's
        // content instead of this one. CopyResource on the immediate
        // context is queue-ordered: it completes in the GPU command
        // stream before any command the handler queues later, so the
        // snapshot is stable for the entire handler lifetime.
        EnsureSnapshotTexture(width, height);
        if (_snapshotTexture is null) return;
        _devices.Context.CopyResource(_snapshotTexture, sourceTexture);

        var textureHandler = TextureArrived;
        if (textureHandler is not null)
        {
            // Invoke subscribers one at a time with a fresh AddRef each.
            // The handler contract is: wrap the pointer as
            // `using var = new ID3D11Texture2D(ptr)` so dispose balances
            // the AddRef. With a single subscriber the old code AddRef'd
            // once around a multicast invocation and that worked; with
            // two subscribers (encoder + self-preview renderer) both
            // handlers dispose a wrapper, each doing a Release, and the
            // original ref underflows — the texture gets freed while our
            // _snapshotTexture pointer still references it, and the next
            // frame's CopyResource walks into freed memory and raises
            // ExecutionEngineException. Walking the invocation list
            // explicitly gives every subscriber its own matched AddRef,
            // independent of how many there are.
            var targets = textureHandler.GetInvocationList();
            var dispatched = false;
            foreach (var target in targets)
            {
                _snapshotTexture.AddRef();
                var disposedByHandler = false;
                try
                {
                    ((TextureArrivedHandler)target).Invoke(
                        _snapshotTexture.NativePointer, width, height, contentTimestamp);
                    disposedByHandler = true;
                    dispatched = true;
                }
                catch (Exception ex)
                {
                    VicoScreenShare.Client.Diagnostics.DebugLog.Write(
                        $"[capture] TextureArrived handler threw: {ex.Message}");
                }
                if (!disposedByHandler)
                {
                    // Handler threw before disposing its wrapper: undo the
                    // AddRef so we don't leak a ref each failure.
                    _snapshotTexture.Release();
                }
            }
            if (dispatched) Interlocked.Increment(ref _dispatchedFrameCount);
        }

        // CPU readback path for subscribers that want BGRA bytes. Still
        // rate-limited to PreviewReadbackFps so the self-preview tile
        // doesn't steal budget from the texture-side encode path, but
        // the texture handler above is unaffected.
        if (FrameArrived is null)
        {
            return;
        }

        var nowTicks = contentTimestamp.Ticks;
        if (_lastPreviewReadbackTicks != long.MinValue &&
            nowTicks - _lastPreviewReadbackTicks < _previewReadbackGapTicks)
        {
            return;
        }
        _lastPreviewReadbackTicks = nowTicks;

        // Read from the snapshot (not the WGC surface which may be
        // recycled by the time the GPU processes this CopyResource).
        _devices.Context.CopyResource(_stagingTexture!, _snapshotTexture!);

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
                contentTimestamp);
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

    private void EnsureSnapshotTexture(int width, int height)
    {
        if (_snapshotTexture is not null && _snapshotWidth == width && _snapshotHeight == height)
        {
            return;
        }
        _snapshotTexture?.Dispose();
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
        _snapshotTexture = _devices.Device.CreateTexture2D(desc);
        _snapshotWidth = width;
        _snapshotHeight = height;
    }
}
