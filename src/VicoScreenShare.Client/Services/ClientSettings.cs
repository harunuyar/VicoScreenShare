using System;
using System.Collections.Generic;
using System.Linq;
using VicoScreenShare.Client.Media;

namespace VicoScreenShare.Client.Services;

/// <summary>
/// Runtime configuration for the client app. Holds the small set of values
/// the view models need that aren't tied to any single operation. Persisted
/// to disk through <see cref="SettingsStore"/>.
/// </summary>
public sealed class ClientSettings
{
    /// <summary>
    /// Legacy single-server field, kept so the first load after upgrading to
    /// the multi-connection schema can migrate it into <see cref="Connections"/>.
    /// After migration this is left at the default. New code should read
    /// <see cref="ActiveConnection"/> instead.
    /// </summary>
    [Obsolete("Use ActiveConnection. Retained for backwards-compat migration.")]
    public Uri ServerUri { get; set; } = new("ws://localhost:5000/ws");

    /// <summary>
    /// User's saved server address book. Exactly one entry is selected as
    /// <see cref="ActiveConnectionId"/>; that's the one the signaling client
    /// connects to. The rest are offered in the connection picker for quick
    /// switching.
    /// </summary>
    public List<ServerConnection> Connections { get; set; } = new();

    /// <summary>Id of the entry in <see cref="Connections"/> the app should use.</summary>
    public Guid? ActiveConnectionId { get; set; }

    /// <summary>Resolve <see cref="ActiveConnectionId"/> → entry, or null if missing.</summary>
    public ServerConnection? ActiveConnection =>
        ActiveConnectionId is Guid id ? Connections.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>
    /// Video pipeline preferences. Lives here so the room view models can
    /// read the current values when they build a
    /// <see cref="CaptureStreamer"/>, and the settings UI can mutate the
    /// same instance in place before asking the <see cref="SettingsStore"/>
    /// to persist it.
    /// </summary>
    public VideoSettings Video { get; set; } = new();
}

/// <summary>
/// A saved entry in the connection address book. Each entry is one server
/// the user has used or wants to reach quickly.
/// </summary>
public sealed class ServerConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-supplied label. Empty → UI falls back to showing the URI host.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>WebSocket signaling endpoint (ws:// or wss://).</summary>
    public Uri Uri { get; set; } = new("ws://localhost:5000/ws");

    /// <summary>
    /// Shared password for servers that enabled <c>RoomServerOptions.AccessPassword</c>.
    /// Null / empty when the server is open. Stored plaintext in %AppData% —
    /// matches the rest of the settings file.
    /// </summary>
    public string? Password { get; set; }
}
