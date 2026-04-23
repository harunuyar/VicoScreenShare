namespace VicoScreenShare.Client.Media.Codecs;

using System;
using Concentus;

/// <summary>
/// Opus decoder backed by Concentus. Matches <see cref="OpusAudioEncoder"/>
/// on the sender — fixed at 48 kHz, variable 20 ms frame length (the
/// decoder reads frame length from the Opus TOC byte, so we don't need
/// to tell it).
/// </summary>
internal sealed class OpusAudioDecoder : IAudioDecoder
{
    // 120 ms at 48 kHz = 5760 samples per channel. Opus's maximum single
    // packet represents a 60 ms frame (2880 samples), so 5760 is
    // double the worst case and leaves headroom for multi-frame packets
    // without a resize. Allocated once, reused per Decode.
    private const int MaxFrameSamplesPerChannel = 5760;

    private readonly IOpusDecoder _decoder;
    private readonly int _channels;
    private readonly short[] _scratch;
    private bool _disposed;

    public OpusAudioDecoder(int channels)
    {
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Opus supports only mono or stereo in WebRTC mode.");
        }

        _channels = channels;
        _decoder = OpusCodecFactory.CreateDecoder(
            48000,
            channels,
            null);
        _scratch = new short[MaxFrameSamplesPerChannel * channels];
    }

    public int SampleRate => 48000;

    public int Channels => _channels;

    public DecodedAudioFrame? Decode(ReadOnlySpan<byte> encoded, uint rtpTimestamp)
    {
        if (_disposed || encoded.IsEmpty)
        {
            return null;
        }

        int samplesPerChannel;
        try
        {
            // frame_size=MaxFrameSamplesPerChannel is the buffer capacity
            // advice; the decoder returns the actual samples decoded.
            samplesPerChannel = _decoder.Decode(encoded, _scratch.AsSpan(), MaxFrameSamplesPerChannel, decode_fec: false);
        }
        catch (OpusException)
        {
            return null;
        }

        if (samplesPerChannel <= 0)
        {
            return null;
        }

        var total = samplesPerChannel * _channels;
        // Copy out to a right-sized array. Allocating per frame keeps the
        // renderer from seeing our reused scratch buffer mutate under
        // its feet when the receiver is feeding ahead while the renderer
        // is still consuming.
        var pcm = new short[total];
        _scratch.AsSpan(0, total).CopyTo(pcm);

        return new DecodedAudioFrame(pcm, samplesPerChannel, _channels, SampleRate, rtpTimestamp);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
    }
}
