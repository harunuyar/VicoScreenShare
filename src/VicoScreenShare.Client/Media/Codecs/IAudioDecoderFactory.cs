namespace VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Builds audio decoders. Analogous to <see cref="IVideoDecoderFactory"/>
/// — the receiver takes a factory rather than a decoder instance so the
/// same SubscriberSession wiring works in tests (in-memory fake decoder)
/// and production (Concentus Opus). The factory also lets the receiver
/// rebuild cleanly if a future design adds on-the-fly codec switching.
/// </summary>
public interface IAudioDecoderFactory
{
    bool IsAvailable { get; }

    /// <summary>
    /// Build a fresh decoder for the given channel count. Sample rate is
    /// fixed at 48 kHz to match the Opus SDP format on the wire; channels
    /// is chosen to match what the publisher negotiated (mono vs stereo).
    /// </summary>
    IAudioDecoder CreateDecoder(int channels);
}
