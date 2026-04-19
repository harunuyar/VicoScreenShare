namespace VicoScreenShare.Client.Media.Codecs;

using System;

/// <summary>
/// One decoded frame. Two pixel carriers, mutually exclusive:
/// <list type="bullet">
/// <item><see cref="Bgra"/> — packed BGRA byte buffer for the CPU pipeline
///       (VPX decoder, MF decoder without a shared D3D device).</item>
/// <item><see cref="TextureSlot"/> — an opaque slot index into the
///       renderer's owned texture ring, for the GPU fast path. When
///       <see cref="TextureSlot"/> &gt;= 0, <see cref="Bgra"/> is
///       conventionally <see cref="Array.Empty{T}"/> and the pixel data
///       lives on the GPU in the slot.</item>
/// </list>
///
/// <see cref="Timestamp"/> is the end-to-end content clock: it equals
/// <c>frame.SystemRelativeTime</c> at the capture source and survives
/// unchanged through encoder SampleTime → RTP → decoder SampleTime →
/// this struct. It is NOT a wall-clock measurement of when the decoder
/// finished producing the frame.
///
/// <see cref="OnDropped"/> is an optional release hook invoked by the
/// playout queue when the frame is *discarded* before paint — overflow,
/// skip, or queue-clear. It is NOT called on normal pop-for-paint; the
/// paint site releases the slot itself after drawing. The GPU path uses
/// this to free a texture-ring slot when the queue evicts a frame that
/// never made it to paint.
/// </summary>
public readonly record struct DecodedVideoFrame(
    byte[] Bgra,
    int Width,
    int Height,
    TimeSpan Timestamp,
    int TextureSlot = -1,
    Action? OnDropped = null);
