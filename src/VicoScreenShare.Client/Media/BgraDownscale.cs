namespace VicoScreenShare.Client.Media;

using System;

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
    /// (stride = width * 4). Hot path on the capture thread when the source
    /// is bigger than the user's encoder cap, so all the pixel arithmetic
    /// runs through unsafe <see cref="uint"/> pointers and the per-pixel
    /// "which source column?" math is hoisted into a precomputed lookup
    /// table (one int per destination column, computed once per call).
    /// At 2560x1392 -> 1920x1044 the old span-indexed version cost ~28 ms
    /// per frame, which capped the encoder to ~28 fps even on an idle 4090.
    /// </summary>
    public static unsafe void Downscale(
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

        // One source-column index per destination column. Computed once per
        // call so the inner loop is a single indexed copy plus an unrelated
        // map lookup, no multiplies / divides per pixel.
        var sxMap = new int[dstWidth];
        for (var dx = 0; dx < dstWidth; dx++)
        {
            var sx = (int)((long)dx * srcWidth / dstWidth);
            if (sx >= srcWidth) sx = srcWidth - 1;
            sxMap[dx] = sx;
        }

        fixed (byte* srcBase = src)
        fixed (byte* dstBase = dst)
        fixed (int* sxBase = sxMap)
        {
            var srcPixels = (uint*)srcBase;
            var dstPixels = (uint*)dstBase;

            for (var dy = 0; dy < dstHeight; dy++)
            {
                var sy = (int)((long)dy * srcHeight / dstHeight);
                if (sy >= srcHeight) sy = srcHeight - 1;

                var srcRow = srcPixels + (long)sy * srcWidth;
                var dstRow = dstPixels + (long)dy * dstWidth;

                // 32-bit-per-pixel copy. One memory read + one memory write
                // per output pixel, no per-channel byte indexing.
                for (var dx = 0; dx < dstWidth; dx++)
                {
                    dstRow[dx] = srcRow[sxBase[dx]];
                }
            }
        }
    }
}
