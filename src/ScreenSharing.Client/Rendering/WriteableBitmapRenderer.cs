using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ScreenSharing.Client.Platform;

namespace ScreenSharing.Client.Rendering;

/// <summary>
/// Bridges <see cref="CaptureFrameData"/> produced by an <see cref="ICaptureSource"/>
/// into an Avalonia <see cref="WriteableBitmap"/> that a view can bind to. Frames
/// arrive on the capture thread; this class marshals to the UI thread and writes
/// pixel bytes into the bitmap using <see cref="WriteableBitmap.Lock"/>. Resizes
/// the bitmap on the fly when the source resolution changes.
/// </summary>
public sealed class WriteableBitmapRenderer : IDisposable
{
    private readonly object _lock = new();
    private WriteableBitmap? _bitmap;
    private byte[]? _staged;
    private int _stagedWidth;
    private int _stagedHeight;
    private int _stagedStride;
    private bool _pendingFrame;
    private bool _disposed;

    public event Action? FrameRendered;

    public WriteableBitmap? CurrentBitmap => _bitmap;

    /// <summary>
    /// Subscribe this renderer to a capture source. Call <see cref="Detach"/> or
    /// dispose to stop receiving frames.
    /// </summary>
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

        // Copy pixel bytes into our own buffer so we can hand off to the UI thread
        // without keeping the source's rented buffer alive.
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

        if (_bitmap is null ||
            _bitmap.PixelSize.Width != width ||
            _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
        }

        using (var fb = _bitmap.Lock())
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

        FrameRendered?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bitmap?.Dispose();
        _bitmap = null;
        _staged = null;
    }
}
