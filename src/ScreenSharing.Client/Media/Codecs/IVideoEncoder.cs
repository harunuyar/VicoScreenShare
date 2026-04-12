using System;

namespace ScreenSharing.Client.Media.Codecs;

/// <summary>
/// One-shot video encoder bound to a fixed output resolution. Callers build a
/// new instance via <see cref="IVideoEncoderFactory.CreateEncoder"/> whenever
/// the encoder target dimensions change — libvpx and Media Foundation both
/// lock their internal state to the first frame's size, so retargeting in
/// place is not a supported path on any backend.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    VideoCodec Codec { get; }

    int Width { get; }

    int Height { get; }

    /// <summary>
    /// Encode one I420-packed frame. Returns the encoded payload (caller owns
    /// the bytes) or null / empty when the codec produced no output for this
    /// input (e.g. the encoder swallowed a frame for rate control). The buffer
    /// must hold <c>width * height * 3 / 2</c> bytes laid out as Y plane,
    /// U plane, V plane.
    /// </summary>
    byte[]? EncodeI420(byte[] i420);
}
