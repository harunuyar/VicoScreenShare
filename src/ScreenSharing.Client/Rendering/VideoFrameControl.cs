using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ScreenSharing.Client.Rendering;

/// <summary>
/// Directly-rendered replacement for <c>&lt;Image Source="{Binding ...}"/&gt;</c>
/// on the receiver video path. The <see cref="Image"/> control's data-binding
/// + property-change + layout-invalidation chain was capping paint rate at
/// ~30 fps on a 144 Hz display with 1440p frames even though the decoder
/// was producing 120+. This control skips all of that and does the minimum
/// necessary work per frame:
///
///  - Hold a reference to a <see cref="WriteableBitmapRenderer"/>.
///  - Subscribe to its <c>FrameRendered</c> event.
///  - On each event, call <see cref="Control.InvalidateVisual"/> — which
///    marks the visual dirty and schedules exactly one repaint at the
///    next compositor tick.
///  - In <see cref="OnRender"/>, read the renderer's
///    <see cref="WriteableBitmapRenderer.CurrentBitmap"/> and draw it with
///    aspect-preserving fit inside the control's bounds.
///
/// The renderer still ping-pongs internally so a mid-paint frame swap
/// doesn't race; we just stop routing the bitmap through data binding.
/// </summary>
public sealed class VideoFrameControl : Control
{
    public static readonly DirectProperty<VideoFrameControl, WriteableBitmapRenderer?> RendererProperty =
        AvaloniaProperty.RegisterDirect<VideoFrameControl, WriteableBitmapRenderer?>(
            nameof(Renderer),
            o => o.Renderer,
            (o, v) => o.Renderer = v);

    private WriteableBitmapRenderer? _renderer;

    public WriteableBitmapRenderer? Renderer
    {
        get => _renderer;
        set
        {
            if (ReferenceEquals(_renderer, value)) return;
            if (_renderer is not null)
            {
                _renderer.FrameRendered -= OnFrameRendered;
            }
            SetAndRaise(RendererProperty, ref _renderer, value);
            if (_renderer is not null)
            {
                _renderer.FrameRendered += OnFrameRendered;
            }
            InvalidateVisual();
        }
    }

    private void OnFrameRendered()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            InvalidateVisual();
        }
        else
        {
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bitmap = _renderer?.CurrentBitmap;
        if (bitmap is null) return;

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var imgW = bitmap.PixelSize.Width;
        var imgH = bitmap.PixelSize.Height;
        if (imgW <= 0 || imgH <= 0) return;

        // Aspect-preserving Uniform fit — same behaviour as
        // Image.Stretch="Uniform" so swapping Image → VideoFrameControl
        // keeps the layout visually identical.
        var imgAspect = (double)imgW / imgH;
        var boundsAspect = bounds.Width / bounds.Height;
        Rect destRect;
        if (imgAspect > boundsAspect)
        {
            var h = bounds.Width / imgAspect;
            destRect = new Rect(bounds.X, bounds.Y + (bounds.Height - h) / 2, bounds.Width, h);
        }
        else
        {
            var w = bounds.Height * imgAspect;
            destRect = new Rect(bounds.X + (bounds.Width - w) / 2, bounds.Y, w, bounds.Height);
        }

        context.DrawImage(bitmap, new Rect(0, 0, imgW, imgH), destRect);
    }
}
