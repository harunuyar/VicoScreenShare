using System;
using System.Collections.Generic;
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
    private readonly SettingsStore _settingsStore;
    private readonly ICaptureProvider? _captureProvider;

    private WebRtcSession? _webRtc;
    private StreamReceiver? _streamReceiver;
    private WriteableBitmapRenderer? _remoteRenderer;
    private WriteableBitmapRenderer? _localRenderer;
    private ICaptureSource? _localCaptureSource;
    private CaptureStreamer? _captureStreamer;
    private bool _negotiationStarted;

    // Track the peer whose video is currently filling the remote tile so we
    // know whose StreamEnded / PeerLeft should clear it. Phase 3 only supports
    // a single active streamer; when Phase 4 adds the grid this becomes a map.
    private Guid? _currentlyStreamingPeerId;

    // Sender side: we generate a fresh stream id on Share so StreamStarted and
    // StreamEnded sent by this client can be paired by observers that care.
    private string? _localStreamId;

    // Viewer-side stale-frame watchdog. If the remote renderer stops producing
    // frames for StaleFrameTimeout, clear the tile even without an explicit
    // StreamEnded or PeerLeft — covers the "publisher's process crashed,
    // network dropped, server not yet detected the disconnect" case.
    private static readonly TimeSpan StaleFrameTimeout = TimeSpan.FromSeconds(2);
    private DispatcherTimer? _staleFrameTimer;
    private DateTime _lastRemoteFrameUtc = DateTime.MinValue;

    public RoomViewModel(
        SignalingClient signaling,
        NavigationService navigation,
        IdentityStore identity,
        Func<SignalingClient> signalingFactory,
        ClientSettings settings,
        SettingsStore settingsStore,
        ICaptureProvider? captureProvider,
        RoomJoined initial)
    {
        _signaling = signaling;
        _navigation = navigation;
        _identity = identity;
        _signalingFactory = signalingFactory;
        _settings = settings;
        _settingsStore = settingsStore;
        _captureProvider = captureProvider;

        _roomId = initial.RoomId;
        _yourPeerId = initial.YourPeerId;

        // Quality controls live right above the Share button so the user can
        // set resolution/fps at the moment of decision. Changes mutate the
        // shared ClientSettings instance in place and persist to disk so the
        // next CaptureStreamer picks them up and the values survive restart.
        ResolutionPresets = new[]
        {
            new ResolutionPreset("480p  (854 x 480)", 854, 480),
            new ResolutionPreset("720p  (1280 x 720)", 1280, 720),
            new ResolutionPreset("1080p (1920 x 1080)", 1920, 1080),
            new ResolutionPreset("1440p (2560 x 1440)", 2560, 1440),
        };
        FrameRatePresets = new[] { 15, 24, 30, 60 };

        _selectedResolution = ResolutionPresets.FirstOrDefault(p =>
            p.Width == _settings.Video.MaxEncoderWidth &&
            p.Height == _settings.Video.MaxEncoderHeight) ?? ResolutionPresets[1];
        _selectedFrameRate = FrameRatePresets.Contains(_settings.Video.TargetFrameRate)
            ? _settings.Video.TargetFrameRate
            : 30;

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
        _signaling.StreamStartedReceived += OnStreamStartedReceived;
        _signaling.StreamEndedReceived += OnStreamEndedReceived;

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

    public IReadOnlyList<ResolutionPreset> ResolutionPresets { get; }

    public IReadOnlyList<int> FrameRatePresets { get; }

    [ObservableProperty]
    private ResolutionPreset _selectedResolution;

    [ObservableProperty]
    private int _selectedFrameRate;

    partial void OnSelectedResolutionChanged(ResolutionPreset value)
    {
        _settings.Video.MaxEncoderWidth = value.Width;
        _settings.Video.MaxEncoderHeight = value.Height;
        TrySaveSettings();
    }

    partial void OnSelectedFrameRateChanged(int value)
    {
        _settings.Video.TargetFrameRate = value;
        TrySaveSettings();
    }

    private void TrySaveSettings()
    {
        try { _settingsStore.Save(_settings); }
        catch { /* persistence failure should not block the session */ }
    }

    public bool CanShareScreen => _captureProvider is not null;

    private void UpdateYouAreHost() => YouAreHost = HostPeerId == YourPeerId;

    private async Task StartWebRtcAsync()
    {
        if (_negotiationStarted) return;
        _negotiationStarted = true;

        // Build the entire media graph in local variables first. Only publish
        // to the class fields after NegotiateAsync has succeeded, so a mid-
        // setup failure leaves _webRtc / _streamReceiver / _remoteRenderer at
        // null and the user cannot accidentally start sharing against a half-
        // initialized peer connection that never completed its handshake.
        WebRtcSession? session = null;
        WriteableBitmapRenderer? renderer = null;
        StreamReceiver? receiver = null;
        try
        {
            session = new WebRtcSession(_signaling, WebRtcRole.Bidirectional);

            renderer = new WriteableBitmapRenderer();
            renderer.FrameRendered += OnRemoteFrameRendered;

            receiver = new StreamReceiver(session.PeerConnection, displayName: "Remote peer");
            renderer.Attach(receiver);
            await receiver.StartAsync().ConfigureAwait(true);

            await session.NegotiateAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);

            _webRtc = session;
            _remoteRenderer = renderer;
            _streamReceiver = receiver;
            MediaStatus = "Waiting for a streamer...";
        }
        catch (Exception ex)
        {
            MediaStatus = $"Media setup failed: {ex.Message}";

            // Tear down the partial graph so nothing lingers. Order matters:
            // detach the receiver from the renderer first, then stop/dispose
            // the receiver, then dispose the renderer, then the session.
            if (renderer is not null && receiver is not null)
            {
                renderer.Detach(receiver);
            }
            if (renderer is not null)
            {
                renderer.FrameRendered -= OnRemoteFrameRendered;
                renderer.Dispose();
            }
            if (receiver is not null)
            {
                try { await receiver.DisposeAsync().ConfigureAwait(true); } catch { }
            }
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(true); } catch { }
            }

            // Allow a later retry (e.g. if the user leaves and comes back) to
            // call StartWebRtcAsync again rather than be stuck at the failed
            // attempt forever.
            _negotiationStarted = false;
        }
    }

    private void OnRemoteFrameRendered()
    {
        if (_remoteRenderer is null) return;
        _lastRemoteFrameUtc = DateTime.UtcNow;
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

            // Wire the source's Closed event so "shared window closed", "user
            // revoked capture", or "monitor disconnected" unwinds the share
            // state the same way the Stop button does, instead of leaving the
            // UI stuck in IsSharing forever.
            source.Closed += OnLocalCaptureClosed;

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
                (duration, payload) => session.PeerConnection.SendVideo(duration, payload),
                _settings.Video);
            _captureStreamer = streamer;
            streamer.Start();

            await source.StartAsync().ConfigureAwait(true);
            IsSharing = true;
            StatusMessage = null;

            // Announce the stream so viewers know who is publishing. If this
            // send fails (e.g. signaling socket raced a disconnect), we still
            // consider the local share started — the server will broadcast
            // StreamEnded on its own when we drop off the session.
            var streamId = Guid.NewGuid().ToString("N");
            _localStreamId = streamId;
            try
            {
                await _signaling.SendStreamStartedAsync(streamId, StreamKind.Screen, hasAudio: false)
                    .ConfigureAwait(true);
            }
            catch
            {
                // Signaling failure doesn't roll back the share; see comment above.
            }
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
            source.Closed -= OnLocalCaptureClosed;
            try { await source.StopAsync().ConfigureAwait(true); } catch { }
            try { await source.DisposeAsync().ConfigureAwait(true); } catch { }
        }

        IsSharing = false;
        LocalStreamLabel = null;
        LocalPreview = null;

        // Announce the stream end exactly once per share. Swallow transport
        // failures — the server already broadcasts StreamEnded for us if we
        // disconnect before this runs, so viewers clear either way.
        var streamId = _localStreamId;
        _localStreamId = null;
        if (streamId is not null && _signaling.IsConnected)
        {
            try
            {
                await _signaling.SendStreamEndedAsync(streamId).ConfigureAwait(true);
            }
            catch { }
        }
    }

    private void OnLocalCaptureClosed()
    {
        // The capture source may raise Closed on a background thread (the
        // Windows.Graphics.Capture session fires on its dispatcher). Marshal
        // to the UI thread and run the same teardown the Stop button does.
        Dispatcher.UIThread.Post(async () =>
        {
            StatusMessage = "Capture source closed.";
            await StopSharingInternalAsync().ConfigureAwait(true);
        });
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

            // If the peer whose video we're rendering just left the room, the
            // server broadcasts StreamEnded before PeerLeft in the graceful
            // path, so this is a double-cover belt-and-braces for the case
            // where ordering is disturbed or StreamEnded is missed.
            if (_currentlyStreamingPeerId == peerId)
            {
                ClearRemoteStream("Stream ended");
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

    private void OnStreamStartedReceived(StreamStarted message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Ignore our own echo if the server ever delivers it (currently it
            // excludes the sender, but we guard anyway).
            if (message.PeerId == YourPeerId) return;

            _currentlyStreamingPeerId = message.PeerId;
            _lastRemoteFrameUtc = DateTime.UtcNow;
            MediaStatus = "Waiting for first frame...";
            StartStaleFrameTimer();
        });
    }

    private void OnStreamEndedReceived(StreamEnded message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_currentlyStreamingPeerId != message.PeerId) return;
            ClearRemoteStream("Stream ended");
        });
    }

    private void ClearRemoteStream(string statusMessage)
    {
        _currentlyStreamingPeerId = null;
        _remoteRenderer?.Clear();
        RemoteStream = null;
        MediaStatus = statusMessage;
        StopStaleFrameTimer();
    }

    private void StartStaleFrameTimer()
    {
        if (_staleFrameTimer is not null) return;
        _staleFrameTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            OnStaleFrameTick);
        _staleFrameTimer.Start();
    }

    private void StopStaleFrameTimer()
    {
        _staleFrameTimer?.Stop();
        _staleFrameTimer = null;
    }

    private void OnStaleFrameTick(object? sender, EventArgs e)
    {
        if (_currentlyStreamingPeerId is null) return;
        if (RemoteStream is null) return;
        if (DateTime.UtcNow - _lastRemoteFrameUtc < StaleFrameTimeout) return;

        // Publisher went dark without a clean StreamEnded — wipe the tile so
        // the viewer doesn't sit on the last decoded frame forever.
        ClearRemoteStream("Stream stalled");
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
        _signaling.StreamStartedReceived -= OnStreamStartedReceived;
        _signaling.StreamEndedReceived -= OnStreamEndedReceived;
        StopStaleFrameTimer();

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

        var home = new HomeViewModel(_identity, _signalingFactory, _navigation, _settings, _settingsStore, _captureProvider);
        _navigation.NavigateTo(home);
    }
}
