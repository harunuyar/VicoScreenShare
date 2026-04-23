namespace VicoScreenShare.Client.Platform;

using VicoScreenShare.Client.Media;

/// <summary>
/// Result of the custom share picker: the target the user selected
/// plus the <see cref="VideoSettings"/> they want to use for this
/// specific share. The settings are a snapshot (not the saved
/// <c>ClientSettings.Video</c> reference) so last-minute overrides in
/// the picker — a lower resolution, a smaller bitrate — apply to this
/// share without mutating the persisted configuration.
/// </summary>
public sealed class PickedCaptureRequest
{
    public PickedCaptureRequest(CaptureTarget target, VideoSettings effectiveSettings)
    {
        Target = target;
        EffectiveSettings = effectiveSettings;
    }

    /// <summary>Window or monitor the user clicked in the picker.</summary>
    public CaptureTarget Target { get; }

    /// <summary>
    /// Video pipeline parameters to use for this share. A copy of the
    /// user's saved settings with any picker-time overrides applied —
    /// callers should not mutate it.
    /// </summary>
    public VideoSettings EffectiveSettings { get; }
}
