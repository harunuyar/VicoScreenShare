using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ScreenSharing.Desktop.App.ViewModels;

public sealed partial class PeerViewModel : ObservableObject
{
    public PeerViewModel(Guid peerId, string displayName, bool isHost, bool isSelf)
    {
        PeerId = peerId;
        _displayName = displayName;
        _isHost = isHost;
        IsSelf = isSelf;
    }

    public Guid PeerId { get; }
    public bool IsSelf { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _isHost;

    /// <summary>
    /// True when this peer is currently streaming. Drives the "●"
    /// live dot on member chips in the room footer so non-streamers
    /// and streamers are visually distinguished at a glance.
    /// </summary>
    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>
    /// False during the server-side reconnect grace window — their WebSocket
    /// dropped but their room slot is still reserved. Member chips render
    /// ghosted until they resume (or the window expires and they're evicted).
    /// </summary>
    [ObservableProperty]
    private bool _isConnected = true;

    /// <summary>
    /// First 1-2 characters of the display name, uppercased, for the
    /// circular avatar on the chip. Recomputed when DisplayName changes.
    /// </summary>
    public string Initials => GetInitials(DisplayName);

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Initials));

    private static string GetInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var trimmed = name.Trim();
        // Grab the first letter, and the first letter of the next word
        // if there is one. "Harun" → "H", "Harun K" → "HK".
        var first = char.ToUpperInvariant(trimmed[0]);
        var spaceIdx = trimmed.IndexOf(' ');
        if (spaceIdx > 0 && spaceIdx + 1 < trimmed.Length)
        {
            return new string(new[] { first, char.ToUpperInvariant(trimmed[spaceIdx + 1]) });
        }
        return first.ToString();
    }
}
