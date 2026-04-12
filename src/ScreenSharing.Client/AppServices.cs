using System;
using ScreenSharing.Client.Media;

namespace ScreenSharing.Client;

/// <summary>
/// Configuration surface for the Avalonia app. Holds the small set of values that
/// view models need but that aren't tied to any single operation. A
/// <see cref="SignalingClient"/> factory lives here rather than a singleton because
/// each create/join operation builds its own signaling instance — see
/// <c>HomeViewModel.RunRoomOperationAsync</c> for the ownership rules.
/// </summary>
public sealed class ClientSettings
{
    /// <summary>
    /// Signaling server WebSocket endpoint. Defaults to localhost:5000 for local dev.
    /// </summary>
    public Uri ServerUri { get; set; } = new("ws://localhost:5000/ws");

    /// <summary>
    /// Video pipeline preferences (resolution cap, fps, future bitrate). Lives
    /// here so the room view models can read the current values when they
    /// build a <see cref="CaptureStreamer"/>, and the settings UI can mutate
    /// the same instance in place before asking the <c>SettingsStore</c> to
    /// persist it.
    /// </summary>
    public VideoSettings Video { get; set; } = new();
}
