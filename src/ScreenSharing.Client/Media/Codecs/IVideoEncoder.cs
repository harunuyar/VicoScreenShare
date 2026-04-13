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
    /// Encode one packed BGRA frame. Returns the encoded payload (caller owns
    /// the bytes) or null / empty when the codec produced no output for this
    /// input (e.g. the encoder swallowed a frame for rate control, or an
    /// async hardware MFT hasn't handed back output yet).
    ///
    /// The encoder implementation is responsible for any color-space
    /// conversion — VP8 wants planar I420 internally, NVENC wants NV12, and
    /// the caller shouldn't have to know which. Taking BGRA here matches
    /// what the capture path produces, so the hottest single-pass conversion
    /// can happen inside the encoder on its own schedule.
    /// </summary>
    byte[]? EncodeBgra(byte[] bgra, int stride);
}
