namespace VicoScreenShare.Client.Platform;

using System;

/// <summary>
/// One audio capture buffer handed off from an <see cref="IAudioCaptureSource"/>
/// to its subscribers. Mirrors <see cref="CaptureFrameData"/> for the
/// audio path: the PCM span is a rented buffer owned by the source, and
/// subscribers must copy whatever they need before the handler returns
/// because the source may recycle the buffer as soon as the event returns.
/// <para>
/// WASAPI delivers native-format PCM in variable-sized chunks (typically
/// 10 ms worth, but drivers are free to vary this). The consumer
/// (<c>AudioStreamer</c>) is responsible for resampling / converting to
/// the codec's expected format and slicing into codec frames.
/// </para>
/// </summary>
public readonly ref struct AudioFrameData
{
    public AudioFrameData(
        ReadOnlySpan<byte> pcm,
        int sampleRate,
        int channels,
        AudioSampleFormat format,
        TimeSpan timestamp)
    {
        Pcm = pcm;
        SampleRate = sampleRate;
        Channels = channels;
        Format = format;
        Timestamp = timestamp;
    }

    /// <summary>Raw PCM bytes in the format described by <see cref="Format"/>.</summary>
    public ReadOnlySpan<byte> Pcm { get; }

    /// <summary>Samples per second per channel (e.g. 48000).</summary>
    public int SampleRate { get; }

    /// <summary>Interleaved channel count (1 = mono, 2 = stereo).</summary>
    public int Channels { get; }

    /// <summary>Sample encoding of <see cref="Pcm"/>.</summary>
    public AudioSampleFormat Format { get; }

    /// <summary>
    /// Monotonic wall-clock timestamp of the first sample in this buffer.
    /// Derived from the same reference as the video path's timestamps so
    /// both streams can be aligned on the publisher; the value is fed to
    /// the encoder verbatim and ends up in the outbound RTCP SR for
    /// receiver-side lip sync.
    /// </summary>
    public TimeSpan Timestamp { get; }
}

/// <summary>
/// Sample format of raw PCM. <see cref="PcmF32Interleaved"/> is what
/// WASAPI loopback produces in shared mode by default (IEEE_FLOAT 32-bit
/// interleaved); <see cref="PcmS16Interleaved"/> is what Opus consumes.
/// </summary>
public enum AudioSampleFormat
{
    /// <summary>16-bit signed integer, little-endian, interleaved.</summary>
    PcmS16Interleaved = 0,

    /// <summary>IEEE 754 32-bit float, little-endian, interleaved.</summary>
    PcmF32Interleaved = 1,
}
