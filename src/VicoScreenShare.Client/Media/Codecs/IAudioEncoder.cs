namespace VicoScreenShare.Client.Media.Codecs;

using System;

/// <summary>
/// One encoded audio frame with the wall-clock <see cref="Timestamp"/> the
/// PCM input was captured at. Audio is latency-sensitive and there are no
/// inter-frame dependencies (every Opus packet stands alone), so the
/// timestamp is always the input timestamp verbatim — unlike the video
/// encoder pipeline where async hardware encoders can reorder.
/// <see cref="Samples"/> is the per-channel count of PCM samples this
/// packet represents (e.g. 960 for a 20 ms frame at 48 kHz). Carried
/// through so the send path can compute the RTP timestamp delta without
/// knowing the sample rate.
/// </summary>
public readonly record struct EncodedAudioFrame(byte[] Bytes, TimeSpan Timestamp, int Samples);

/// <summary>
/// One-shot audio encoder bound to a fixed sample rate and channel layout.
/// Callers build a new instance via <see cref="IAudioEncoderFactory.CreateEncoder"/>
/// whenever settings change — Opus locks its internal state to the
/// parameters it was created with, so retargeting in place is not a
/// supported path on any backend.
/// <para>
/// The encoder always accepts S16 interleaved PCM. The publisher's
/// capture source produces whatever format the audio device's mix engine
/// is running in (typically 32-bit float), so the <see cref="AudioStreamer"/>
/// inserts an <c>IAudioResampler</c> in front of the encoder to convert.
/// </para>
/// </summary>
public interface IAudioEncoder : IDisposable
{
    /// <summary>RTP clock rate for the encoded stream. Opus is fixed at 48 000.</summary>
    int SampleRate { get; }

    /// <summary>Number of interleaved channels in the PCM input.</summary>
    int Channels { get; }

    /// <summary>
    /// Per-channel samples per encoded frame (e.g. 960 at 48 kHz for 20 ms).
    /// The caller must slice its input into exactly this many samples per
    /// channel per <see cref="EncodePcm"/> call — Opus is a fixed-frame codec
    /// and there is no equivalent of VBR frame length at the API boundary.
    /// </summary>
    int FrameSamples { get; }

    /// <summary>
    /// Encode exactly <see cref="FrameSamples"/> × <see cref="Channels"/>
    /// interleaved S16 samples. Returns the encoded payload with the
    /// propagated input timestamp, or null on encoder error (logged by
    /// the implementation). The returned byte array is owned by the
    /// caller and sized to the encoded length.
    /// </summary>
    EncodedAudioFrame? EncodePcm(ReadOnlySpan<short> interleavedPcm, TimeSpan inputTimestamp);

    /// <summary>
    /// Reconfigure the encoder's target bitrate in bits per second while it
    /// is running. Provided for symmetry with <see cref="IVideoEncoder"/>;
    /// audio's share of the pipe is small enough that the publisher's
    /// loss-based ABR does not drive this in the first cut, but the hook is
    /// here so a future audio-aware ABR can plug in without an interface
    /// break. Default is a no-op so implementations that don't support
    /// runtime bitrate control can ignore the call.
    /// </summary>
    void UpdateBitrate(int bitsPerSecond) { }
}
