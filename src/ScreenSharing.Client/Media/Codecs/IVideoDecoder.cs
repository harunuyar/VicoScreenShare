using System;
using System.Collections.Generic;

namespace ScreenSharing.Client.Media.Codecs;

/// <summary>
/// Video decoder for a specific codec. <see cref="Decode"/> may return zero,
/// one, or multiple frames per call — libvpx and some Media Foundation paths
/// can split input across multiple outputs (or buffer initial inputs before
/// producing anything).
/// </summary>
public interface IVideoDecoder : IDisposable
{
    VideoCodec Codec { get; }

    IReadOnlyList<DecodedVideoFrame> Decode(byte[] encodedSample);
}
