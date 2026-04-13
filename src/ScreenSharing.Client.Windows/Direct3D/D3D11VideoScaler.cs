using System;
using ScreenSharing.Client.Diagnostics;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenSharing.Client.Windows.Direct3D;

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
public sealed class D3D11VideoScaler : IDisposable
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

        // One-time processor state: full-frame output rect, progressive,
        // normal output rate. This is the minimum the driver needs to run
        // a BGRA → BGRA bilinear scale via VideoProcessorBlt.
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, destWidth, destHeight));
        _videoContext.VideoProcessorSetStreamOutputRate(_processor, 0, VideoProcessorOutputRate.Normal, true, null);

        DebugLog.Write($"[scaler] D3D11 video processor built {sourceWidth}x{sourceHeight} -> {destWidth}x{destHeight}");
    }

    public int SourceWidth => _sourceWidth;

    public int SourceHeight => _sourceHeight;

    public int DestWidth => _destWidth;

    public int DestHeight => _destHeight;

    /// <summary>
    /// Scale <paramref name="sourceTexture"/> into <paramref name="destTexture"/>.
    /// Both textures must live on the device this scaler was built against.
    /// The destination texture is cached against its output view, so calling
    /// this repeatedly with the same destination is cheap.
    /// </summary>
    public void Process(ID3D11Texture2D sourceTexture, ID3D11Texture2D destTexture)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(D3D11VideoScaler));

        // Input view — per-call because WGC hands out a fresh surface each frame.
        var inputViewDesc = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
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
