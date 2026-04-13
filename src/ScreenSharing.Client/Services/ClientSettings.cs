using System;
using ScreenSharing.Client.Media;

namespace ScreenSharing.Client.Services;

/// <summary>
/// Runtime configuration for the client app. Holds the small set of values
/// the view models need that aren't tied to any single operation. Persisted
/// to disk through <see cref="SettingsStore"/>.
/// </summary>
public sealed class ClientSettings
{
    /// <summary>
    /// Signaling server WebSocket endpoint. Defaults to localhost:5000 for
    /// local dev.
    /// </summary>
    public Uri ServerUri { get; set; } = new("ws://localhost:5000/ws");

    /// <summary>
    /// Video pipeline preferences. Lives here so the room view models can
    /// read the current values when they build a
    /// <see cref="CaptureStreamer"/>, and the settings UI can mutate the
    /// same instance in place before asking the <see cref="SettingsStore"/>
    /// to persist it.
    /// </summary>
    public VideoSettings Video { get; set; } = new();
}
