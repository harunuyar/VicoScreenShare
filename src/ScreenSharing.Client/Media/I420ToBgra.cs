using System;

namespace ScreenSharing.Client.Media;

/// <summary>
/// Converts planar I420 (YUV420p) frames back into 8-bit BGRA, the format Avalonia's
/// <c>WriteableBitmap</c> with <c>Bgra8888</c> consumes. BT.601 full-range inverse
/// transform in 16.16 fixed point, with chroma upsampled by nearest-neighbor
/// (each 2x2 output block shares the same U/V sample). This matches the forward
/// transform in <see cref="BgraToI420"/> so a round-trip of a solid-color frame
/// recovers within a small rounding window.
/// </summary>
public static class I420ToBgra
{
    public static int RequiredBgraSize(int width, int height) => width * height * 4;

    public static void Convert(
        ReadOnlySpan<byte> i420Source,
        int width,
        int height,
        Span<byte> bgraDestination,
        int bgraStrideBytes)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (bgraStrideBytes < width * 4) throw new ArgumentOutOfRangeException(nameof(bgraStrideBytes));
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var required = width * height + 2 * chromaWidth * chromaHeight;
        if (i420Source.Length < required)
        {
            throw new ArgumentException($"I420 source must be at least {required} bytes.", nameof(i420Source));
        }

        var yPlane = i420Source.Slice(0, width * height);
        var uPlane = i420Source.Slice(width * height, chromaWidth * chromaHeight);
        var vPlane = i420Source.Slice(width * height + chromaWidth * chromaHeight, chromaWidth * chromaHeight);

        for (var y = 0; y < height; y++)
        {
            var yRow = yPlane.Slice(y * width, width);
            var uRow = uPlane.Slice((y / 2) * chromaWidth, chromaWidth);
            var vRow = vPlane.Slice((y / 2) * chromaWidth, chromaWidth);
            var dstRow = bgraDestination.Slice(y * bgraStrideBytes, width * 4);

            for (var x = 0; x < width; x++)
            {
                int yVal = yRow[x];
                int uVal = uRow[x / 2] - 128;
                int vVal = vRow[x / 2] - 128;

                // BT.601 full range:
                //   R = Y + 1.402 V
                //   G = Y - 0.344 U - 0.714 V
                //   B = Y + 1.772 U
                var r = yVal + ((91881 * vVal) >> 16);
                var g = yVal - ((22554 * uVal + 46802 * vVal) >> 16);
                var b = yVal + ((116130 * uVal) >> 16);

                if (r < 0) r = 0; else if (r > 255) r = 255;
                if (g < 0) g = 0; else if (g > 255) g = 255;
                if (b < 0) b = 0; else if (b > 255) b = 255;

                dstRow[x * 4 + 0] = (byte)b;
                dstRow[x * 4 + 1] = (byte)g;
                dstRow[x * 4 + 2] = (byte)r;
                dstRow[x * 4 + 3] = 0xFF;
            }
        }
    }
}
