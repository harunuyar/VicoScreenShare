using System;
using System.Collections.Generic;

namespace ScreenSharing.Client.Media.Codecs;

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
}
