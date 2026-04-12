using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Rendering;
using ScreenSharing.Client.Services;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;

namespace ScreenSharing.Client.ViewModels;

public sealed partial class RoomViewModel : ViewModelBase
{
    private readonly SignalingClient _signaling;
    private readonly NavigationService _navigation;
    private readonly IdentityStore _identity;
    private readonly Func<SignalingClient> _signalingFactory;
    private readonly ClientSettings _settings;
    private readonly ICaptureProvider? _captureProvider;

    private WebRtcSession? _webRtc;
    private StreamReceiver? _streamReceiver;
    private WriteableBitmapRenderer? _remoteRenderer;
    private WriteableBitmapRenderer? _localRenderer;
    private ICaptureSource? _localCaptureSource;
    private CaptureStreamer? _captureStreamer;
    private bool _negotiationStarted;

    public RoomViewModel(
        SignalingClient signaling,
        NavigationService navigation,
        IdentityStore identity,
        Func<SignalingClient> signalingFactory,
        ClientSettings settings,
        ICaptureProvider? captureProvider,
        RoomJoined initial)
    {
        _signaling = signaling;
        _navigation = navigation;
        _identity = identity;
        _signalingFactory = signalingFactory;
        _settings = settings;
        _captureProvider = captureProvider;

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

        // Kick off the peer connection handshake in the background so the room is
        // ready to both send (when the user hits Share) and receive (whenever any
        // other peer's RTP flows through the SFU).
        _ = StartWebRtcAsync();
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

    [ObservableProperty]
    private string? _mediaStatus = "Connecting media...";

    [ObservableProperty]
    private bool _isSharing;

    [ObservableProperty]
    private Bitmap? _remoteStream;

    [ObservableProperty]
    private Bitmap? _localPreview;

    [ObservableProperty]
    private string? _localStreamLabel;

    public bool CanShareScreen => _captureProvider is not null;

    private void UpdateYouAreHost() => YouAreHost = HostPeerId == YourPeerId;

    private async Task StartWebRtcAsync()
    {
        if (_negotiationStarted) return;
        _negotiationStarted = true;

        try
        {
            var session = new WebRtcSession(_signaling, WebRtcRole.Bidirectional);
            _webRtc = session;

            var renderer = new WriteableBitmapRenderer();
            renderer.FrameRendered += OnRemoteFrameRendered;
            _remoteRenderer = renderer;

            var receiver = new StreamReceiver(session.PeerConnection, displayName: "Remote peer");
            renderer.Attach(receiver);
            _streamReceiver = receiver;
            await receiver.StartAsync().ConfigureAwait(true);

            await session.NegotiateAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);
            MediaStatus = "Waiting for a streamer...";
        }
        catch (Exception ex)
        {
            MediaStatus = $"Media setup failed: {ex.Message}";
        }
    }

    private void OnRemoteFrameRendered()
    {
        if (_remoteRenderer is null) return;
        var next = _remoteRenderer.CurrentBitmap;
        if (Dispatcher.UIThread.CheckAccess())
        {
            RemoteStream = next;
            MediaStatus = null;
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                RemoteStream = next;
                MediaStatus = null;
            });
        }
    }

    [RelayCommand]
    private async Task ShareScreenAsync()
    {
        if (IsSharing || _captureProvider is null || _webRtc is null)
        {
            return;
        }

        ICaptureSource? source = null;
        try
        {
            source = await _captureProvider.PickSourceAsync().ConfigureAwait(true);
            if (source is null)
            {
                StatusMessage = "Picker cancelled.";
                return;
            }

            LocalStreamLabel = source.DisplayName;
            _localCaptureSource = source;

            // Local preview: attach a second renderer to the same capture source
            // so the streamer sees their own feed while the CaptureStreamer
            // forwards encoded samples out over the peer connection.
            var localRenderer = new WriteableBitmapRenderer();
            localRenderer.FrameRendered += OnLocalFrameRendered;
            localRenderer.Attach(source);
            _localRenderer = localRenderer;

            var session = _webRtc;
            var streamer = new CaptureStreamer(
                source,
                (duration, payload) => session.PeerConnection.SendVideo(duration, payload));
            _captureStreamer = streamer;
            streamer.Start();

            await source.StartAsync().ConfigureAwait(true);
            IsSharing = true;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Share failed: {ex.Message}";
            await StopSharingInternalAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task StopSharingAsync() => StopSharingInternalAsync();

    private async Task StopSharingInternalAsync()
    {
        var streamer = _captureStreamer;
        var source = _localCaptureSource;
        var localRenderer = _localRenderer;

        _captureStreamer = null;
        _localCaptureSource = null;
        _localRenderer = null;

        // Tear down order: detach the streamer's frame subscription FIRST so no
        // more frames hit the native encoder, then dispose the streamer (which
        // takes its own encode lock and waits for any in-flight frame), then
        // stop and dispose the source, then the local renderer.
        if (streamer is not null)
        {
            streamer.Stop();
            streamer.Dispose();
        }

        if (localRenderer is not null && source is not null)
        {
            localRenderer.Detach(source);
        }
        if (localRenderer is not null)
        {
            localRenderer.FrameRendered -= OnLocalFrameRendered;
            localRenderer.Dispose();
        }

        if (source is not null)
        {
            try { await source.StopAsync().ConfigureAwait(true); } catch { }
            try { await source.DisposeAsync().ConfigureAwait(true); } catch { }
        }

        IsSharing = false;
        LocalStreamLabel = null;
        LocalPreview = null;
    }

    private void OnLocalFrameRendered()
    {
        var next = _localRenderer?.CurrentBitmap;
        if (next is null) return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            LocalPreview = next;
        }
        else
        {
            Dispatcher.UIThread.Post(() => LocalPreview = next);
        }
    }

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

        await StopSharingInternalAsync().ConfigureAwait(true);

        if (_streamReceiver is not null && _remoteRenderer is not null)
        {
            _remoteRenderer.Detach(_streamReceiver);
            _remoteRenderer.FrameRendered -= OnRemoteFrameRendered;
            _remoteRenderer.Dispose();
            _remoteRenderer = null;
        }
        if (_streamReceiver is not null)
        {
            await _streamReceiver.DisposeAsync().ConfigureAwait(true);
            _streamReceiver = null;
        }
        if (_webRtc is not null)
        {
            await _webRtc.DisposeAsync().ConfigureAwait(true);
            _webRtc = null;
        }

        await _signaling.DisposeAsync();

        var home = new HomeViewModel(_identity, _signalingFactory, _navigation, _settings, _captureProvider);
        _navigation.NavigateTo(home);
    }
}
