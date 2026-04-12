using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenSharing.Client.Services;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;

namespace ScreenSharing.Client.ViewModels;

public sealed partial class RoomViewModel : ViewModelBase
{
    private readonly SignalingClient _signaling;
    private readonly NavigationService _navigation;

    public RoomViewModel(SignalingClient signaling, NavigationService navigation, RoomJoined initial)
    {
        _signaling = signaling;
        _navigation = navigation;

        _roomId = initial.RoomId;
        _yourPeerId = initial.YourPeerId;

        Peers = new ObservableCollection<PeerViewModel>(
            initial.Peers.Select(p => new PeerViewModel(
                p.PeerId,
                p.DisplayName,
                p.IsHost,
                p.PeerId == initial.YourPeerId)));

        HostPeerId = Peers.FirstOrDefault(p => p.IsHost)?.PeerId;
        UpdateYouAreHost();

        _signaling.PeerJoined += OnPeerJoined;
        _signaling.PeerLeft += OnPeerLeft;
        _signaling.ServerError += OnServerError;
        _signaling.ConnectionLost += OnConnectionLost;
    }

    public ObservableCollection<PeerViewModel> Peers { get; }

    [ObservableProperty]
    private string _roomId;

    [ObservableProperty]
    private Guid _yourPeerId;

    [ObservableProperty]
    private Guid? _hostPeerId;

    [ObservableProperty]
    private bool _youAreHost;

    [ObservableProperty]
    private string? _statusMessage;

    private void UpdateYouAreHost() => YouAreHost = HostPeerId == YourPeerId;

    private void OnPeerJoined(PeerInfo peer)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Peers.Any(p => p.PeerId == peer.PeerId)) return;
            Peers.Add(new PeerViewModel(peer.PeerId, peer.DisplayName, peer.IsHost, peer.PeerId == YourPeerId));
        });
    }

    private void OnPeerLeft(Guid peerId, Guid? newHostPeerId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var existing = Peers.FirstOrDefault(p => p.PeerId == peerId);
            if (existing is not null)
            {
                Peers.Remove(existing);
            }
            if (newHostPeerId.HasValue)
            {
                HostPeerId = newHostPeerId;
                foreach (var p in Peers)
                {
                    p.IsHost = p.PeerId == newHostPeerId.Value;
                }
                UpdateYouAreHost();
                if (YouAreHost)
                {
                    StatusMessage = "Previous host left. You are now the host.";
                }
            }
        });
    }

    private void OnServerError(ErrorCode code, string message)
    {
        Dispatcher.UIThread.Post(() => StatusMessage = $"Server error: {message}");
    }

    private void OnConnectionLost(string? reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = string.IsNullOrEmpty(reason)
                ? "Disconnected."
                : $"Disconnected: {reason}";
        });
    }

    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        _signaling.PeerJoined -= OnPeerJoined;
        _signaling.PeerLeft -= OnPeerLeft;
        _signaling.ServerError -= OnServerError;
        _signaling.ConnectionLost -= OnConnectionLost;

        await _signaling.DisposeAsync();

        var home = new HomeViewModel(
            AppServices.Identity,
            new SignalingClient(),
            _navigation,
            AppServices.Settings);
        _navigation.NavigateTo(home);
    }
}
