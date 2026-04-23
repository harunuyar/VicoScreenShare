namespace VicoScreenShare.Client.Media;

/// <summary>
/// User-configurable audio pipeline settings. Drives the Opus encoder's
/// bitrate and channel layout, the Opus application hint (voip vs
/// general audio, which affects SILK vs CELT mode selection in the codec),
/// and whether the publisher captures system loopback at all. Persisted
/// to disk alongside <see cref="VideoSettings"/> via
/// <see cref="Services.SettingsStore"/>.
/// <para>
/// Shared-content audio is opt-in: <see cref="Enabled"/> defaults to
/// false. A user who shares their screen without thinking about audio
/// gets the same silent stream the app always produced; enabling audio
/// is a deliberate settings toggle. This keeps the privacy posture
/// conservative by default — a YouTube tab playing in the background
/// doesn't leak audio into a pair-programming room unless the sharer asks
/// for it.
/// </para>
/// </summary>
public sealed class AudioSettings
{
    /// <summary>
    /// Master switch for shared-content audio. When false the publisher
    /// never opens a loopback capture device and <c>StreamStarted.HasAudio</c>
    /// is false — the audio <c>m=</c> section in the SDP still negotiates
    /// (so toggling does not force renegotiation of a live video stream)
    /// but no bytes flow.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Target Opus bitrate in bits per second. 96 kbps stereo is a
    /// reasonable default for mixed content (music + speech); 64 kbps is
    /// adequate for voice-dominant material. Opus handles everything from
    /// 8 kbps up to 510 kbps but 192 kbps is the practical upper limit
    /// where further bits stop producing audible improvements.
    /// </summary>
    public int TargetBitrate { get; set; } = 96_000;

    /// <summary>
    /// Stereo (true) vs mono (false). Stereo doubles the PCM input size
    /// but does not quite double the encoded bitrate — Opus's joint
    /// stereo mode exploits channel correlation. Default on because
    /// shared-content audio is typically stereo music / game / video.
    /// </summary>
    public bool Stereo { get; set; } = true;

    /// <summary>
    /// Opus frame duration in milliseconds. WebRTC's canonical value is
    /// 20 ms and every browser implementation expects this. Range
    /// 2.5/5/10/20/40/60 is supported by Opus but anything other than 20
    /// reduces interop, so the UI should not expose this — the field
    /// exists here only for experimentation from the MediaHarness.
    /// </summary>
    public int FrameDurationMs { get; set; } = 20;

    /// <summary>
    /// Hint to the Opus encoder whether to optimize for voice (favors
    /// SILK, lower bitrate for speech) or mixed content (allows CELT /
    /// hybrid modes, better on music). Loopback capture is almost always
    /// "general audio" — a user sharing a game, a YouTube video, a
    /// Spotify track. Default is <see cref="OpusApplicationMode.GeneralAudio"/>.
    /// </summary>
    public OpusApplicationMode Application { get; set; } = OpusApplicationMode.GeneralAudio;
}

/// <summary>
/// Maps to Concentus's <c>OpusApplication</c> enum without leaking the
/// library's type through the settings surface — the settings POCO gets
/// persisted by name and we don't want a Concentus renaming to silently
/// break user configs on upgrade.
/// </summary>
public enum OpusApplicationMode
{
    /// <summary>Tuned for voice (SILK-preferred). Lower latency, better
    /// for speech-only scenarios like a conference call.</summary>
    Voip = 0,

    /// <summary>Tuned for mixed content (music + speech). Default for
    /// shared-content audio.</summary>
    GeneralAudio = 1,

    /// <summary>Strict low-latency mode (CELT-only, skips the SILK path).
    /// Not recommended for shared-content audio — loses quality for a
    /// latency win we don't need since WebRTC's jitter buffer already
    /// dominates end-to-end delay.</summary>
    LowDelay = 2,
}
