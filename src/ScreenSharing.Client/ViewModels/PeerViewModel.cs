using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ScreenSharing.Client.ViewModels;

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
}
