using System;

namespace ScreenSharing.Client.Media;

/// <summary>
/// Area-average BGRA downscale used to clamp oversized captures (4K monitors,
/// ultrawide screens) to a manageable encoder resolution. Nearest-neighbor is
/// good enough for a diagnostic first cut and keeps the sender hot path fast.
/// </summary>
public static class BgraDownscale
{
    /// <summary>
    /// Choose a target size that preserves aspect ratio, fits within
    /// <paramref name="maxWidth"/> × <paramref name="maxHeight"/>, and has both
    /// dimensions even (libvpx's VP8 encoder wants even dims for I420 chroma
    /// subsampling).
    /// </summary>
    public static (int width, int height) FitWithin(int srcWidth, int srcHeight, int maxWidth, int maxHeight)
    {
        if (srcWidth <= maxWidth && srcHeight <= maxHeight)
        {
            return (srcWidth & ~1, srcHeight & ~1);
        }

        var scaleX = (double)maxWidth / srcWidth;
        var scaleY = (double)maxHeight / srcHeight;
        var scale = Math.Min(scaleX, scaleY);

        var w = (int)Math.Floor(srcWidth * scale) & ~1;
        var h = (int)Math.Floor(srcHeight * scale) & ~1;
        if (w < 2) w = 2;
        if (h < 2) h = 2;
        return (w, h);
    }

    /// <summary>
    /// Nearest-neighbor BGRA downscale. Source and destination are both packed
    /// (stride = width * 4). Fast enough to run on the capture thread for
    /// typical ratios (4K source -> 720p output).
    /// </summary>
    public static void Downscale(
        ReadOnlySpan<byte> src,
        int srcWidth,
        int srcHeight,
        Span<byte> dst,
        int dstWidth,
        int dstHeight)
    {
        if (dst.Length < dstWidth * dstHeight * 4)
        {
            throw new ArgumentException("dst buffer too small", nameof(dst));
        }

        for (var dy = 0; dy < dstHeight; dy++)
        {
            var sy = (int)((long)dy * srcHeight / dstHeight);
            if (sy >= srcHeight) sy = srcHeight - 1;
            var srcRowStart = sy * srcWidth * 4;
            var dstRowStart = dy * dstWidth * 4;

            for (var dx = 0; dx < dstWidth; dx++)
            {
                var sx = (int)((long)dx * srcWidth / dstWidth);
                if (sx >= srcWidth) sx = srcWidth - 1;
                var s = srcRowStart + sx * 4;
                var d = dstRowStart + dx * 4;
                dst[d + 0] = src[s + 0];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }
    }
}
