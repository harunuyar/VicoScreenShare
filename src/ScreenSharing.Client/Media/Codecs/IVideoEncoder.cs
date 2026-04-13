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
    /// True when <see cref="EncodeTexture"/> is usable. False implementations
    /// (software VP8, or a hardware MFT that couldn't attach a D3D device)
    /// still accept the CPU path via <see cref="EncodeBgra"/>.
    /// </summary>
    bool SupportsTextureInput { get; }

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

    /// <summary>
    /// Encode one BGRA texture on the encoder's D3D11 device. This is the
    /// zero-copy fast path: the capture source hands us its framepool
    /// texture and the encoder downscales (via the D3D11 Video Processor)
    /// and color-converts on the GPU internally, with no CPU roundtrip and
    /// no compact pass. Throws <see cref="NotSupportedException"/> when
    /// <see cref="SupportsTextureInput"/> is false.
    /// </summary>
    byte[]? EncodeTexture(IntPtr nativeTexture, int sourceWidth, int sourceHeight);
}
