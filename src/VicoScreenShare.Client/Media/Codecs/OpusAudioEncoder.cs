namespace VicoScreenShare.Client.Media.Codecs;

using System;
using Concentus;
using Concentus.Enums;

/// <summary>
/// Opus encoder backed by Concentus (a pure-managed C# port of libopus).
/// Bound at construction to a fixed sample rate (48 kHz, matching
/// WebRTC's canonical Opus format), channel count, frame duration, and
/// bitrate — Concentus locks its internal state to these, and an in-place
/// retarget is not supported.
/// <para>
/// Produces RFC 6716 Opus packets suitable for direct passage through
/// SIPSorcery's <c>RTCPeerConnection.SendAudio(duration, payload)</c>,
/// carried as RTP payload type 111 (the value baked into
/// <c>AudioCommonlyUsedFormats.OpusWebRTC</c> which the four peer
/// connections advertise).
/// </para>
/// </summary>
internal sealed class OpusAudioEncoder : IAudioEncoder
{
    // Opus specifies an absolute maximum packet size of 1275 bytes for a
    // single standard-rate frame (48 kHz, 60 ms). Our frames are 20 ms
    // each so the real worst case is ~480 bytes, but allocating the
    // spec-defined maximum keeps the code robust against future frame
    // duration changes and costs nothing — it's a stack-friendly buffer.
    private const int MaxEncodedBytes = 1275;

    private readonly IOpusEncoder _encoder;
    private readonly int _frameSamples;
    private readonly int _channels;
    private bool _disposed;

    public OpusAudioEncoder(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _channels = settings.Stereo ? 2 : 1;
        // Opus frame length in samples: duration_ms * sample_rate / 1000.
        // Frame duration is validated here to catch invalid config early
        // rather than on the first encode call. Concentus silently
        // accepts odd frame sizes and produces garbage in that case.
        _frameSamples = settings.FrameDurationMs * 48000 / 1000;
        if (_frameSamples is not (120 or 240 or 480 or 960 or 1920 or 2880))
        {
            throw new ArgumentException(
                $"Opus frame duration {settings.FrameDurationMs} ms produces {_frameSamples} samples, " +
                "which is not one of the supported frame lengths (2.5/5/10/20/40/60 ms at 48 kHz).",
                nameof(settings));
        }

        _encoder = OpusCodecFactory.CreateEncoder(
            48000,
            _channels,
            MapApplication(settings.Application),
            null);

        _encoder.Bitrate = settings.TargetBitrate;
        // Signal-type music: loopback capture is typically YouTube / games /
        // Spotify. For voice-tilted content (a Teams meeting playing on the
        // screen) Opus's heuristics will still route to SILK internally; the
        // hint is a prior, not a hard switch.
        _encoder.SignalType = settings.Application == OpusApplicationMode.Voip
            ? OpusSignal.OPUS_SIGNAL_VOICE
            : OpusSignal.OPUS_SIGNAL_MUSIC;
        // In-band FEC: Opus carries a low-bitrate copy of the previous
        // frame inside the current packet. On packet loss the decoder can
        // substitute the FEC copy so a single lost packet is inaudible.
        // Cheap at our bitrates — ~1 kbps of overhead — and the whole
        // reason "audio survives what video can't" on bad links.
        _encoder.UseInbandFEC = true;
        // Packet-loss percent tells Opus how much bit-budget to allocate
        // to FEC. 5% is a reasonable always-on baseline; the ABR path
        // does not feed audio today, so a static value is correct.
        _encoder.PacketLossPercent = 5;
        // Complexity 10 (max) is still real-time on any modern CPU for a
        // single 20 ms stereo frame — Concentus measurements show ≈ 0.3 ms
        // per frame at complexity 10 on a 2020 laptop. Worth the quality.
        _encoder.Complexity = 10;
    }

    public int SampleRate => 48000;

    public int Channels => _channels;

    public int FrameSamples => _frameSamples;

    public EncodedAudioFrame? EncodePcm(ReadOnlySpan<short> interleavedPcm, TimeSpan inputTimestamp)
    {
        if (_disposed)
        {
            return null;
        }

        var required = _frameSamples * _channels;
        if (interleavedPcm.Length != required)
        {
            throw new ArgumentException(
                $"Opus encoder expects exactly {required} interleaved samples " +
                $"({_frameSamples} per channel × {_channels} channels) per frame, " +
                $"got {interleavedPcm.Length}.",
                nameof(interleavedPcm));
        }

        Span<byte> scratch = stackalloc byte[MaxEncodedBytes];
        int written;
        try
        {
            written = _encoder.Encode(interleavedPcm, _frameSamples, scratch, scratch.Length);
        }
        catch (OpusException)
        {
            // Malformed state or catastrophic internal error. Opus is
            // stateful only across packets in FEC mode; the next frame
            // can still encode cleanly, so we swallow and return null
            // rather than tearing down the encoder.
            return null;
        }

        if (written <= 0)
        {
            return null;
        }

        // Concentus returns only the byte count; copy out to a
        // caller-owned array sized to fit. The callback in AudioStreamer
        // hands this straight to SendAudio which serializes into RTP —
        // no further reuse, so the allocation is per-frame and measured.
        var payload = new byte[written];
        scratch[..written].CopyTo(payload);
        return new EncodedAudioFrame(payload, inputTimestamp, _frameSamples);
    }

    public void UpdateBitrate(int bitsPerSecond)
    {
        if (_disposed)
        {
            return;
        }
        // Clamp to Opus's documented operating range. Below 6 kbps the
        // codec refuses to accept the setting; above 510 kbps is
        // effectively lossless and meaningless.
        var clamped = Math.Clamp(bitsPerSecond, 6_000, 510_000);
        try { _encoder.Bitrate = clamped; } catch (OpusException) { }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // Concentus's IOpusEncoder does not implement IDisposable; the
        // concrete Concentus.Structs.OpusEncoder does but we use the
        // interface for testability. There is nothing to unmanaged-free
        // here — the encoder state is a managed struct graph — so the
        // marker is enough to gate future Encode calls.
    }

    private static OpusApplication MapApplication(OpusApplicationMode mode) => mode switch
    {
        OpusApplicationMode.Voip => OpusApplication.OPUS_APPLICATION_VOIP,
        OpusApplicationMode.GeneralAudio => OpusApplication.OPUS_APPLICATION_AUDIO,
        OpusApplicationMode.LowDelay => OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY,
        _ => OpusApplication.OPUS_APPLICATION_AUDIO,
    };
}
