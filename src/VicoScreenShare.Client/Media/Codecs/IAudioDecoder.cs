namespace VicoScreenShare.Client.Media.Codecs;

using System;

/// <summary>
/// One decoded audio frame: interleaved S16 PCM and the RTP timestamp the
/// packet arrived with. The renderer compares consecutive timestamps to
/// detect missing frames and to drive its own paint clock; the sample
/// count also gives the renderer a duration in RTP units without it
/// needing to know the sample rate.
/// </summary>
public readonly record struct DecodedAudioFrame(
    short[] Pcm,
    int Samples,
    int Channels,
    int SampleRate,
    uint RtpTimestamp);

/// <summary>
/// Audio decoder bound to a fixed sample rate and channel layout. Opus
/// packets have no inter-dependencies, so a single <see cref="Decode"/>
/// call produces exactly one frame (or null when the payload is
/// malformed). This is simpler than the video decoder contract, which has
/// to handle 0-N outputs per input because of MF's pipelined path.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>RTP clock rate. Opus is always 48 000.</summary>
    int SampleRate { get; }

    /// <summary>Number of interleaved output channels.</summary>
    int Channels { get; }

    /// <summary>
    /// Decode one encoded packet. Returns the PCM frame plus the RTP
    /// timestamp the packet was carried with (propagated verbatim so the
    /// receiver can reorder the small jitter buffer by timestamp). Returns
    /// null when the payload is unparseable — the receiver treats this as
    /// a dropped frame and moves on.
    /// </summary>
    DecodedAudioFrame? Decode(ReadOnlySpan<byte> encoded, uint rtpTimestamp);
}
