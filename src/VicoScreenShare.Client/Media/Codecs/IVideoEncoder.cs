namespace VicoScreenShare.Client.Media.Codecs;

using System;

/// <summary>
/// One encoded video frame with the **content** timestamp it was captured
/// at — i.e. <see cref="Timestamp"/> identifies the moment the picture
/// inside <see cref="Bytes"/> was on the screen, not the moment the
/// encoder happened to finish producing it. Async encoders are pipelined
/// (NVENC keeps several frames in flight) so the bytes coming out of any
/// given <c>EncodeTexture</c> call are usually for an *earlier* input
/// frame, not the one whose call returned them. Carrying the source
/// timestamp through the encoder + decoder + pacer is what keeps the
/// receiver's paint cadence aligned with the actual motion content.
/// </summary>
public readonly record struct EncodedFrame(byte[] Bytes, TimeSpan Timestamp);

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
    /// Encode one packed BGRA frame tagged with the capture-side content
    /// timestamp. Returns the encoded payload (caller owns the bytes) plus
    /// the propagated content timestamp, or null when the codec produced no
    /// output for this input (rate-control swallow, async pump warmup, etc).
    ///
    /// For async hardware encoders the returned <see cref="EncodedFrame.Timestamp"/>
    /// may be for an *earlier* input than the one this call submitted, because
    /// the encoder pipeline is pipelined and the event pump reads SampleTime
    /// off whichever output sample is ready. Sync encoders echo
    /// <paramref name="inputTimestamp"/> verbatim.
    /// </summary>
    EncodedFrame? EncodeBgra(byte[] bgra, int stride, TimeSpan inputTimestamp);

    /// <summary>
    /// Encode one BGRA texture on the encoder's D3D11 device. Same timestamp
    /// contract as <see cref="EncodeBgra"/>. Throws
    /// <see cref="NotSupportedException"/> when
    /// <see cref="SupportsTextureInput"/> is false.
    /// </summary>
    EncodedFrame? EncodeTexture(IntPtr nativeTexture, int sourceWidth, int sourceHeight, TimeSpan inputTimestamp);

    /// <summary>
    /// Request the encoder emit the next frame as an IDR/keyframe. Used when
    /// the receive side reports unrecoverable packet loss via RTCP PLI —
    /// the sender forces a fresh GOP so the decoder can re-sync without
    /// waiting for the scheduled interval. Safe to call from any thread.
    /// Default implementation is a no-op for codecs that don't support
    /// runtime keyframe control (VP8 via libvpx).
    /// </summary>
    void RequestKeyframe() { }

    /// <summary>
    /// Reconfigure the encoder's target bitrate in bits per second while
    /// it's running. Called by the adaptive-bitrate controller when loss
    /// on the upstream path forces us to back off (or recover toward the
    /// original target as loss clears). Safe to call from any thread.
    /// Default implementation is a no-op for codecs that don't support
    /// runtime bitrate control (VP8 via libvpx in this build).
    /// </summary>
    void UpdateBitrate(int bitsPerSecond) { }
}

/// <summary>
/// Optional interface for async hardware encoders (NVENC, QSV, AMF).
/// When implemented, <see cref="OutputAvailable"/> fires from the
/// encoder's internal event pump the instant an encoded frame is ready,
/// allowing the caller to dispatch it immediately instead of waiting
/// for the next <see cref="IVideoEncoder.EncodeTexture"/> poll.
/// Reduces enc-out → dispatch latency from ~16 ms (one capture
/// interval) to ~0 ms.
/// </summary>
public interface IAsyncEncodedOutputSource
{
    event Action? OutputAvailable;
    bool TryDequeueEncoded(out EncodedFrame frame);
}
