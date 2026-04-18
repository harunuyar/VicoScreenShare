using System;

namespace VicoScreenShare.Client.Media;

/// <summary>
/// Converts 8-bit BGRA frames (the format the Windows capture path produces) into
/// planar I420 / YUV420p — the format VP8/VP9/H.264 encoders want. Pure managed,
/// no intrinsics yet. Uses BT.601 full-range coefficients, which matches what
/// SIPSorcery's VpxVideoEncoder expects.
/// </summary>
public static class BgraToI420
{
    /// <summary>
    /// Required output buffer size for an I420 frame of the given dimensions.
    /// I420 is three planes: Y = w*h, U = (w/2)*(h/2), V = (w/2)*(h/2).
    /// </summary>
    public static int RequiredOutputSize(int width, int height)
    {
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        return width * height + 2 * chromaWidth * chromaHeight;
    }

    /// <summary>Convenience overload that allocates the I420 buffer for you.</summary>
    public static byte[] ConvertToArray(ReadOnlySpan<byte> bgraSource, int width, int height, int bgraStrideBytes)
    {
        var result = new byte[RequiredOutputSize(width, height)];
        Convert(bgraSource, width, height, bgraStrideBytes, result);
        return result;
    }

    /// <summary>
    /// Converts a BGRA8 source buffer into I420. The destination must be at least
    /// <see cref="RequiredOutputSize"/> bytes. Chroma is subsampled by averaging
    /// each 2x2 block of source pixels. Even/odd dimensions handled via
    /// <c>(dim + 1) / 2</c> rounding.
    /// </summary>
    public static void Convert(
        ReadOnlySpan<byte> bgraSource,
        int width,
        int height,
        int bgraStrideBytes,
        Span<byte> i420Destination)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (bgraStrideBytes < width * 4) throw new ArgumentOutOfRangeException(nameof(bgraStrideBytes));
        var required = RequiredOutputSize(width, height);
        if (i420Destination.Length < required)
        {
            throw new ArgumentException($"I420 destination must be at least {required} bytes.", nameof(i420Destination));
        }

        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var yPlane = i420Destination.Slice(0, width * height);
        var uPlane = i420Destination.Slice(width * height, chromaWidth * chromaHeight);
        var vPlane = i420Destination.Slice(width * height + chromaWidth * chromaHeight, chromaWidth * chromaHeight);

        // Y plane: per-pixel luma. BT.601: Y = 0.299 R + 0.587 G + 0.114 B
        for (var y = 0; y < height; y++)
        {
            var srcRow = bgraSource.Slice(y * bgraStrideBytes, width * 4);
            var dstRow = yPlane.Slice(y * width, width);
            for (var x = 0; x < width; x++)
            {
                var b = srcRow[x * 4 + 0];
                var g = srcRow[x * 4 + 1];
                var r = srcRow[x * 4 + 2];
                // 16.16 fixed point: 0.299=19595, 0.587=38470, 0.114=7471 (total 65536)
                var yVal = (19595 * r + 38470 * g + 7471 * b + 32768) >> 16;
                if (yVal < 0) yVal = 0;
                else if (yVal > 255) yVal = 255;
                dstRow[x] = (byte)yVal;
            }
        }

        // U / V planes: 2x2 averaged chroma. BT.601:
        // U = -0.169 R - 0.331 G + 0.500 B + 128
        // V =  0.500 R - 0.419 G - 0.081 B + 128
        for (var cy = 0; cy < chromaHeight; cy++)
        {
            var y0 = cy * 2;
            var y1 = Math.Min(y0 + 1, height - 1);
            var uRow = uPlane.Slice(cy * chromaWidth, chromaWidth);
            var vRow = vPlane.Slice(cy * chromaWidth, chromaWidth);
            for (var cx = 0; cx < chromaWidth; cx++)
            {
                var x0 = cx * 2;
                var x1 = Math.Min(x0 + 1, width - 1);

                int sumR = 0, sumG = 0, sumB = 0;
                AccumulatePixel(bgraSource, y0, x0, bgraStrideBytes, ref sumR, ref sumG, ref sumB);
                AccumulatePixel(bgraSource, y0, x1, bgraStrideBytes, ref sumR, ref sumG, ref sumB);
                AccumulatePixel(bgraSource, y1, x0, bgraStrideBytes, ref sumR, ref sumG, ref sumB);
                AccumulatePixel(bgraSource, y1, x1, bgraStrideBytes, ref sumR, ref sumG, ref sumB);

                var avgR = sumR >> 2;
                var avgG = sumG >> 2;
                var avgB = sumB >> 2;

                var uVal = ((-11059 * avgR - 21709 * avgG + 32768 * avgB + 8388608) >> 16);
                var vVal = ((32768 * avgR - 27439 * avgG - 5329 * avgB + 8388608) >> 16);

                if (uVal < 0) uVal = 0; else if (uVal > 255) uVal = 255;
                if (vVal < 0) vVal = 0; else if (vVal > 255) vVal = 255;

                uRow[cx] = (byte)uVal;
                vRow[cx] = (byte)vVal;
            }
        }
    }

    private static void AccumulatePixel(
        ReadOnlySpan<byte> src,
        int y,
        int x,
        int stride,
        ref int sumR,
        ref int sumG,
        ref int sumB)
    {
        var offset = y * stride + x * 4;
        sumB += src[offset + 0];
        sumG += src[offset + 1];
        sumR += src[offset + 2];
    }
}
