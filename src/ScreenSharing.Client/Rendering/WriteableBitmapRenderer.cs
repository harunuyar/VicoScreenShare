using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ScreenSharing.Client.Platform;

namespace ScreenSharing.Client.Rendering;

/// <summary>
/// Bridges <see cref="CaptureFrameData"/> produced by an <see cref="ICaptureSource"/>
/// into an Avalonia bitmap that a view can bind to. Frames arrive on the capture
/// thread; this class marshals to the UI thread and writes pixel bytes via
/// <see cref="WriteableBitmap.Lock"/>.
///
/// Ping-pongs two bitmaps because Avalonia's <see cref="Avalonia.Controls.Image"/>
/// only re-renders when its <c>Source</c> property changes reference. Writing new
/// pixels into the same <see cref="WriteableBitmap"/> produces a frozen preview;
/// alternating between two bitmaps gives the binding a fresh reference every
/// frame so <see cref="FrameRendered"/> consumers see real updates.
/// </summary>
public sealed class WriteableBitmapRenderer : IDisposable
{
    private readonly object _lock = new();
    // Single bitmap (was a 2-slot ping-pong). The ping-pong only existed
    // to work around Image.Source="{Binding …}" not noticing in-place
    // pixel updates — with VideoFrameControl reading CurrentBitmap
    // directly in its OnRender, the reference never needs to change.
    // Keeping one bitmap halves the allocated pixel footprint and removes
    // the branch that picks a slot on each frame.
    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _current;

    private byte[]? _staged;
    private int _stagedWidth;
    private int _stagedHeight;
    private int _stagedStride;
    private bool _pendingFrame;
    private bool _disposed;

    // Diagnostics for "decoder says 120 fps but it doesn't feel like 120 fps".
    // The stats overlay reads these to show "input fps / paint fps / dropped"
    // so we can tell whether the decoder is the cap or the UI thread is.
    //
    //   InputFrameCount  — every frame the decoder (or capture) handed us.
    //   PaintedFrameCount— every frame that actually made it to the UI-thread
    //                      pixel copy (WriteableBitmap.Lock + memcpy). Frames
    //                      coalesced by the _pendingFrame flag never count
    //                      here, so InputFrameCount - PaintedFrameCount is the
    //                      number of frames dropped on the floor because the
    //                      UI thread couldn't keep up.
    //   LastPaintMs      — elapsed wall-clock time the most recent paint spent
    //                      inside Lock+memcpy+Unlock. At 1080p the copy is
    //                      ~8 MB; if this creeps above the frame gap, the UI
    //                      thread becomes the bottleneck.
    public long InputFrameCount { get; private set; }

    public long PaintedFrameCount { get; private set; }

    public long DroppedFrameCount => InputFrameCount - PaintedFrameCount;

    public double LastPaintMs { get; private set; }

    public event Action? FrameRendered;

    public WriteableBitmap? CurrentBitmap => _current;

    /// <summary>
    /// Drops the reference to the last rendered frame so a subsequent read of
    /// <see cref="CurrentBitmap"/> returns null. Pooled bitmaps stay alive for
    /// reuse on the next incoming frame; this only clears the published handle
    /// so the view binding can go back to showing nothing.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _current = null;
        }
    }

    public void Attach(ICaptureSource source)
    {
        source.FrameArrived += OnFrameArrived;
    }

    public void Detach(ICaptureSource source)
    {
        source.FrameArrived -= OnFrameArrived;
    }

    private void OnFrameArrived(in CaptureFrameData frame)
    {
        if (_disposed) return;
        if (frame.Format != CaptureFramePixelFormat.Bgra8) return;

        lock (_lock)
        {
            InputFrameCount++;
            var required = frame.Height * frame.StrideBytes;
            if (_staged is null || _staged.Length < required)
            {
                _staged = new byte[required];
            }
            frame.Pixels.Slice(0, required).CopyTo(_staged);
            _stagedWidth = frame.Width;
            _stagedHeight = frame.Height;
            _stagedStride = frame.StrideBytes;

            // Coalesce: if a previous frame is already queued for the UI
            // thread, overwrite _staged but don't queue a second Post. This
            // is intentional — we never want to fall behind — but it means
            // bursty input to a slow UI loses frames. DroppedFrameCount
            // makes that loss visible in the stats overlay.
            if (_pendingFrame) return;
            _pendingFrame = true;
        }

        Dispatcher.UIThread.Post(PushStagedFrame, DispatcherPriority.Render);
    }

    private void PushStagedFrame()
    {
        WriteableBitmap? rendered;
        var paintStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // Everything below runs under the same lock the capture thread uses to
        // write _staged so a second FrameArrived cannot overwrite our source
        // buffer mid-copy. The critical section is a single pooled memcpy (tens
        // of microseconds to ~1 ms for 1080p) — short enough to block the
        // capture thread only imperceptibly, and the correctness is worth the
        // trivial contention.
        lock (_lock)
        {
            _pendingFrame = false;
            if (_staged is null) return;

            var width = _stagedWidth;
            var height = _stagedHeight;
            var stride = _stagedStride;
            var pixels = _staged;

            var target = _bitmap;
            if (target is null ||
                target.PixelSize.Width != width ||
                target.PixelSize.Height != height)
            {
                target?.Dispose();
                target = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);
                _bitmap = target;
            }

            using (var fb = target.Lock())
            {
                unsafe
                {
                    var rowBytes = width * 4;
                    for (var y = 0; y < height; y++)
                    {
                        var src = new ReadOnlySpan<byte>(pixels, y * stride, rowBytes);
                        var dst = new Span<byte>((byte*)fb.Address + (long)y * fb.RowBytes, rowBytes);
                        src.CopyTo(dst);
                    }
                }
            }

            _current = target;
            rendered = target;
            PaintedFrameCount++;
        }

        var paintEnd = System.Diagnostics.Stopwatch.GetTimestamp();
        LastPaintMs = (paintEnd - paintStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        if (rendered is not null)
        {
            FrameRendered?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bitmap?.Dispose();
        _bitmap = null;
        _current = null;
        _staged = null;
    }
}
