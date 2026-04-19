namespace VicoScreenShare.Client.Windows.Direct3D;

using System;
using VicoScreenShare.Client.Diagnostics;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// GPU-side BGRA → BGRA scaler built on the Direct3D 11 Video Processor.
/// Replaces the CPU <c>BgraDownscale</c> pass on the capture hot path.
///
/// Runs on the dedicated video hardware (NVIDIA / AMD / Intel video engines),
/// not the 3D graphics pipeline — at 4K60 it costs ~0.5 ms per frame vs
/// the CPU's ~3–4 ms for a scalar loop, and it ships with real filter
/// quality (bilinear / bicubic / Lanczos) that our nearest-neighbor
/// fallback cannot match on text.
///
/// Lifecycle: one instance per (input size, output size) pair. Recreate
/// if either changes. The output view is cached against a fixed destination
/// texture; input views are built per-frame because WGC hands out a fresh
/// surface each frame.
/// </summary>
public sealed class D3D11VideoScaler : ITextureScaler
{
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _destWidth;
    private readonly int _destHeight;

    private ID3D11Texture2D? _cachedDestTexture;
    private ID3D11VideoProcessorOutputView? _cachedOutputView;
    private bool _disposed;

    public D3D11VideoScaler(
        ID3D11Device device,
        int sourceWidth,
        int sourceHeight,
        int destWidth,
        int destHeight)
    {
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _destWidth = destWidth;
        _destHeight = destHeight;

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        var contentDesc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = (uint)sourceWidth,
            InputHeight = (uint)sourceHeight,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)destWidth,
            OutputHeight = (uint)destHeight,
            Usage = VideoUsage.PlaybackNormal,
        };

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(contentDesc);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        // One-time processor state. Target rect covers the full output;
        // stream dest rect defaults to the full output too (the caller
        // can override via Process(src, dst, destRect) to letterbox).
        // Background color is black so letterbox bars render clean
        // instead of leaking whatever was in the back buffer previously.
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, destWidth, destHeight));
        _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, false,
            new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
        _videoContext.VideoProcessorSetStreamOutputRate(_processor, 0, VideoProcessorOutputRate.Normal, true, null);
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new RawRect(0, 0, destWidth, destHeight));

        DebugLog.Write($"[scaler] D3D11 video processor built {sourceWidth}x{sourceHeight} -> {destWidth}x{destHeight}");
    }

    public int SourceWidth => _sourceWidth;

    public int SourceHeight => _sourceHeight;

    public int DestWidth => _destWidth;

    public int DestHeight => _destHeight;

    public void Process(ID3D11Texture2D sourceTexture, ID3D11Texture2D destTexture)
        => Process(sourceTexture, destTexture, null, 0);

    public void Process(ID3D11Texture2D sourceTexture, ID3D11Texture2D destTexture, uint sourceArraySlice)
        => Process(sourceTexture, destTexture, null, sourceArraySlice);

    /// <summary>
    /// Scale <paramref name="sourceTexture"/> into <paramref name="destTexture"/>.
    /// Both textures must live on the device this scaler was built against.
    ///
    /// <paramref name="destRect"/> is an optional letterbox rect inside
    /// the destination. When non-null the stream is written into that
    /// sub-rectangle; the rest of the destination fills with the
    /// background color set in the ctor (black). Used by the WPF
    /// receiver renderer to preserve source aspect ratio when the room
    /// tile has a different aspect.
    /// </summary>
    public void Process(ID3D11Texture2D sourceTexture, ID3D11Texture2D destTexture, RawRect? destRect)
        => Process(sourceTexture, destTexture, destRect, 0);

    /// <summary>
    /// Scale <paramref name="sourceTexture"/> into <paramref name="destTexture"/>.
    /// <paramref name="sourceArraySlice"/> selects which array slice of the
    /// source texture to read from — DXVA decoders output into texture arrays
    /// where each DPB slot is a different slice. Pass the value from
    /// <c>IMFDXGIBuffer.SubresourceIndex</c>.
    /// </summary>
    public void Process(ID3D11Texture2D sourceTexture, ID3D11Texture2D destTexture, RawRect? destRect, uint sourceArraySlice)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(D3D11VideoScaler));

        // Input view — per-call because the source texture (and its
        // array slice) changes every frame.
        var inputViewDesc = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = sourceArraySlice },
        };
        using var inputView = _videoDevice.CreateVideoProcessorInputView(sourceTexture, _enumerator, inputViewDesc);

        // Cache the output view against the destination texture identity —
        // the caller reuses the same destination texture across frames so
        // this allocation happens once.
        if (!ReferenceEquals(_cachedDestTexture, destTexture) || _cachedOutputView is null)
        {
            _cachedOutputView?.Dispose();
            var outputViewDesc = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            };
            _cachedOutputView = _videoDevice.CreateVideoProcessorOutputView(destTexture, _enumerator, outputViewDesc);
            _cachedDestTexture = destTexture;
        }

        // Update the stream dest rect per-frame when letterboxing. When
        // the WPF tile's aspect ratio changes (e.g. maximize) the renderer
        // recomputes destRect and passes it in; we don't rebuild the scaler.
        if (destRect is RawRect r)
        {
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, r);
        }

        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = inputView,
        };

        _videoContext.VideoProcessorBlt(_processor, _cachedOutputView, 0, 1, new[] { stream });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cachedOutputView?.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
