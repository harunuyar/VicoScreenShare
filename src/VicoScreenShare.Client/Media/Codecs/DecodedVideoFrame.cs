namespace VicoScreenShare.Client.Media.Codecs;

using System;

/// <summary>
/// One decoded frame as a packed BGRA byte buffer plus the content
/// timestamp of the input that produced it. Decoder implementations
/// normalize whatever native pixel layout they get (BGR, NV12, I420, …)
/// into BGRA and propagate the source timestamp through the codec
/// pipeline so the receiver's present loop can reproduce the original
/// capture cadence on paint.
///
/// <see cref="Timestamp"/> is the end-to-end content clock: it equals
/// <c>frame.SystemRelativeTime</c> at the capture source and survives
/// unchanged through encoder SampleTime → RTP → decoder SampleTime →
/// this struct. It is NOT a wall-clock measurement of when the decoder
/// finished producing the frame.
/// </summary>
public readonly record struct DecodedVideoFrame(
    byte[] Bgra,
    int Width,
    int Height,
    TimeSpan Timestamp);
