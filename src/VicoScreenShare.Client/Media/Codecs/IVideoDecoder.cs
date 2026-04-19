namespace VicoScreenShare.Client.Media.Codecs;

using System;
using System.Collections.Generic;

/// <summary>
/// Video decoder for a specific codec. <see cref="Decode"/> may return zero,
/// one, or multiple frames per call — libvpx and some Media Foundation paths
/// can split input across multiple outputs (or buffer initial inputs before
/// producing anything). Each returned frame carries its OWN propagated
/// content timestamp via <see cref="DecodedVideoFrame.Timestamp"/>, read
/// from the MF output sample's SampleTime, which the underlying decoder
/// MFT copies from the input sample we stamped with
/// <paramref name="inputTimestamp"/>. When a call yields several outputs
/// at once each one is labeled with the timestamp of the input that
/// actually produced it, NOT the current call's value.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    VideoCodec Codec { get; }

    IReadOnlyList<DecodedVideoFrame> Decode(byte[] encodedSample, TimeSpan inputTimestamp);

    /// <summary>
    /// Reset the decoder's internal state. Discards any queued input/output
    /// samples and clears any error state so the next <see cref="Decode"/>
    /// can start from a keyframe with a clean slate. Callers must follow
    /// Flush with a keyframe; feeding inter-frames immediately after a flush
    /// yields missing-reference errors.
    /// </summary>
    void Flush() { }

    /// <summary>
    /// Optional sink for GPU-resident BGRA output. When set, the decoder
    /// invokes this delegate synchronously during <see cref="Decode"/> for
    /// each frame it produces and passes an <c>ID3D11Texture2D</c> pointer
    /// on the decoder's shared device. The texture is valid ONLY for the
    /// duration of the call — subscribers must GPU-copy (<c>CopyResource</c>)
    /// or otherwise consume it before returning. The CPU <c>byte[]</c> path
    /// in the <see cref="Decode"/> return value is suppressed for frames
    /// delivered this way, so callers need not worry about double emission.
    /// Decoders that don't support GPU output can leave the default no-op
    /// setter; the caller will simply continue using the CPU path.
    /// </summary>
    Action<IntPtr, int, int, TimeSpan>? GpuOutputHandler
    {
        get => null;
        set { }
    }
}
