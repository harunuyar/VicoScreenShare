using System;

namespace ScreenSharing.Client.Platform;

/// <summary>
/// Single captured frame handed off from an <see cref="ICaptureSource"/> to its
/// subscribers. The payload is a rented buffer owned by the source; subscribers must
/// copy whatever they need before the handler returns, because the source may recycle
/// the buffer as soon as the event returns.
/// </summary>
public readonly ref struct CaptureFrameData
{
    public CaptureFrameData(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int strideBytes,
        CaptureFramePixelFormat format,
        TimeSpan timestamp)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        StrideBytes = strideBytes;
        Format = format;
        Timestamp = timestamp;
    }

    public ReadOnlySpan<byte> Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public int StrideBytes { get; }

    public CaptureFramePixelFormat Format { get; }

    public TimeSpan Timestamp { get; }
}

public enum CaptureFramePixelFormat
{
    /// <summary>8-bit per channel BGRA, matches Avalonia's Bgra8888 and Windows default desktop format.</summary>
    Bgra8,
}
