namespace VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Constructs audio encoders for a given <see cref="AudioSettings"/>
/// snapshot. Analogous to <see cref="IVideoEncoderFactory"/>: tests and
/// the MediaHarness depend on the factory indirection so they can swap in
/// fakes without taking a native-codec dependency, and production code
/// rebuilds the encoder when settings change rather than mutating a
/// live instance.
/// </summary>
public interface IAudioEncoderFactory
{
    /// <summary>
    /// True if this factory can produce a working encoder on the current
    /// runtime. For Opus via Concentus this is always true — Concentus is
    /// pure managed C# and has no OS preconditions.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Build a fresh encoder. Caller disposes it when finished. Throws
    /// when <see cref="IsAvailable"/> is false.
    /// </summary>
    IAudioEncoder CreateEncoder(AudioSettings settings);
}
