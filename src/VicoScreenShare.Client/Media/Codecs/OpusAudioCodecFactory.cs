namespace VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Factory for the Concentus-backed Opus encoder + decoder pair. Always
/// available because Concentus is pure managed C# — no native
/// dependency, no OS preconditions, works the same in xUnit on CI as it
/// does on a Windows publisher.
/// </summary>
public sealed class OpusAudioCodecFactory : IAudioEncoderFactory, IAudioDecoderFactory
{
    public bool IsAvailable => true;

    public IAudioEncoder CreateEncoder(AudioSettings settings) => new OpusAudioEncoder(settings);

    public IAudioDecoder CreateDecoder(int channels) => new OpusAudioDecoder(channels);
}
