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
    private readonly WriteableBitmap?[] _pool = new WriteableBitmap?[2];
    private int _nextSlot;
    private WriteableBitmap? _current;

    private byte[]? _staged;
    private int _stagedWidth;
    private int _stagedHeight;
    private int _stagedStride;
    private bool _pendingFrame;
    private bool _disposed;

    public event Action? FrameRendered;

    public WriteableBitmap? CurrentBitmap => _current;

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
            var required = frame.Height * frame.StrideBytes;
            if (_staged is null || _staged.Length < required)
            {
                _staged = new byte[required];
            }
            frame.Pixels.Slice(0, required).CopyTo(_staged);
            _stagedWidth = frame.Width;
            _stagedHeight = frame.Height;
            _stagedStride = frame.StrideBytes;

            if (_pendingFrame) return;
            _pendingFrame = true;
        }

        Dispatcher.UIThread.Post(PushStagedFrame, DispatcherPriority.Render);
    }

    private void PushStagedFrame()
    {
        byte[]? pixels;
        int width, height, stride;
        lock (_lock)
        {
            _pendingFrame = false;
            if (_staged is null) return;
            pixels = _staged;
            width = _stagedWidth;
            height = _stagedHeight;
            stride = _stagedStride;
        }

        var slot = _nextSlot;
        _nextSlot = 1 - _nextSlot;

        var target = _pool[slot];
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
            _pool[slot] = target;
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
        FrameRendered?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool[0]?.Dispose();
        _pool[1]?.Dispose();
        _pool[0] = null;
        _pool[1] = null;
        _current = null;
        _staged = null;
    }
}
