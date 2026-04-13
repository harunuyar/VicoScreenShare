using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenSharing.Client;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Services;
using ScreenSharing.Desktop.Wpf.Services;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;

namespace ScreenSharing.Desktop.Wpf.ViewModels;

/// <summary>
/// Room session view model. Owns the WebRTC peer connection, the stream
/// receiver, and the capture → encode → send pipeline. The
/// <c>D3DImageVideoRenderer</c> on the room view subscribes to
/// <see cref="StreamReceiver"/> directly once negotiation succeeds.
/// </summary>
public sealed partial class RoomViewModel : ViewModelBase
{
    private readonly SignalingClient _signaling;
    private readonly INavigationHost _navigation;
    private readonly IdentityStore _identity;
    private readonly Func<SignalingClient> _signalingFactory;
    private readonly ClientSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ICaptureProvider? _captureProvider;
    private readonly Dispatcher _dispatcher;

    private WebRtcSession? _webRtc;
    private StreamReceiver? _streamReceiver;
    private ICaptureSource? _localCaptureSource;
    private CaptureStreamer? _captureStreamer;
    private bool _negotiationStarted;

    private VideoCodec _sessionCodec;
    private IVideoEncoderFactory _encoderFactory;
    private IVideoDecoderFactory _decoderFactory;

    private Guid? _currentlyStreamingPeerId;
    private string? _localStreamId;

    private static readonly TimeSpan StaleFrameTimeout = TimeSpan.FromSeconds(2);
    private DispatcherTimer? _staleFrameTimer;
    private DateTime _lastRemoteFrameUtc = DateTime.MinValue;

    private DispatcherTimer? _statsTimer;
    private long _statsPrevSenderFrames;
    private long _statsPrevSenderBytes;
    private long _statsPrevReceiverFrames;
    private long _statsPrevReceiverBytes;
    private DateTime _statsPrevTickUtc = DateTime.MinValue;

    public RoomViewModel(
        SignalingClient signaling,
        INavigationHost navigation,
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
        _dispatcher = Dispatcher.CurrentDispatcher;

        var catalog = ClientHost.VideoCodecCatalog ?? new VideoCodecCatalog();
        var resolved = catalog.ResolveOrFallback(settings.Video.Codec);
        _sessionCodec = resolved.selected;
        _encoderFactory = resolved.encoderFactory;
        _decoderFactory = resolved.decoderFactory;

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
        _signaling.StreamStartedReceived += OnStreamStartedReceived;
        _signaling.StreamEndedReceived += OnStreamEndedReceived;

        var existingStreamer = initial.Peers.FirstOrDefault(
            p => p.IsStreaming && p.PeerId != initial.YourPeerId);
        if (existingStreamer is not null)
        {
            _currentlyStreamingPeerId = existingStreamer.PeerId;
            _lastRemoteFrameUtc = DateTime.UtcNow;
            MediaStatus = "Joining mid-stream...";
            StartStaleFrameTimer();
        }

        _ = StartWebRtcAsync();
        StartStatsTimer();
    }

    public ObservableCollection<PeerViewModel> Peers { get; }

    /// <summary>
    /// Current remote receiver, or null before negotiation. The room view's
    /// D3DImageVideoRenderer picks this up to subscribe for decoded frames.
    /// </summary>
    public StreamReceiver? StreamReceiver => _streamReceiver;

    [ObservableProperty] private string _roomId;
    [ObservableProperty] private string _copyButtonText = "Copy";
    [ObservableProperty] private Guid _yourPeerId;
    [ObservableProperty] private Guid? _hostPeerId;
    [ObservableProperty] private bool _youAreHost;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _mediaStatus = "Connecting media...";
    [ObservableProperty] private bool _isSharing;
    [ObservableProperty] private bool _hasRemoteStream;
    [ObservableProperty] private string? _localStreamLabel;
    [ObservableProperty] private string _senderStats = "—";
    [ObservableProperty] private string _receiverStats = "—";

    /// <summary>
    /// Extra "paint fps" lines appended to the receiver stats block,
    /// produced by <see cref="Views.RoomView"/>'s poll of the
    /// <see cref="Rendering.D3DImageVideoRenderer"/>. The VM can't read
    /// the renderer directly because the renderer is a visual-tree
    /// concern — instead the view pushes its own reading into this
    /// property and <see cref="BuildReceiverStats"/> concatenates it.
    /// </summary>
    [ObservableProperty] private string? _renderStatsLine;

    public bool CanShareScreen => _captureProvider is not null;

    private void UpdateYouAreHost() => YouAreHost = HostPeerId == YourPeerId;

    private async Task StartWebRtcAsync()
    {
        if (_negotiationStarted) return;
        _negotiationStarted = true;

        WebRtcSession? session = null;
        StreamReceiver? receiver = null;
        try
        {
            session = new WebRtcSession(_signaling, WebRtcRole.Bidirectional, _sessionCodec);

            receiver = new StreamReceiver(session.PeerConnection, _decoderFactory, displayName: "Remote peer");
            receiver.FrameArrived += OnRemoteFrameArrived;
            await receiver.StartAsync().ConfigureAwait(true);

            await session.NegotiateAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);

            _webRtc = session;
            _streamReceiver = receiver;
            OnPropertyChanged(nameof(StreamReceiver));
            MediaStatus = "Waiting for a streamer...";
        }
        catch (Exception ex)
        {
            MediaStatus = $"Media setup failed: {ex.Message}";
            if (receiver is not null)
            {
                try { receiver.FrameArrived -= OnRemoteFrameArrived; } catch { }
                try { await receiver.DisposeAsync().ConfigureAwait(true); } catch { }
            }
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(true); } catch { }
            }
            _negotiationStarted = false;
        }
    }

    private void OnRemoteFrameArrived(in CaptureFrameData frame)
    {
        _lastRemoteFrameUtc = DateTime.UtcNow;
        if (HasRemoteStream && MediaStatus is null) return;
        if (_dispatcher.CheckAccess())
        {
            HasRemoteStream = true;
            MediaStatus = null;
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                HasRemoteStream = true;
                MediaStatus = null;
            }));
        }
    }

    [RelayCommand]
    private async Task ShareScreenAsync()
    {
        if (IsSharing || _captureProvider is null || _webRtc is null) return;
        try
        {
            // DDA path — bypasses DWM compose throttling so the capture rate
            // matches the monitor's refresh rate even when the user's cursor
            // is stationary. Correct backend for gaming.
            var source = await _captureProvider.PickScreenAsync().ConfigureAwait(true);
            if (source is null)
            {
                StatusMessage = "Share cancelled.";
                return;
            }
            await StartSharingAsync(source).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Share failed: {ex.Message}";
            await StopSharingInternalAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ShareWindowAsync()
    {
        if (IsSharing || _captureProvider is null || _webRtc is null) return;
        try
        {
            // WGC path via the system picker — still the only way to get
            // per-window capture. Expect lower fps than the DDA path when
            // the window is idle, because WGC rides on DWM's compose rate.
            var source = await _captureProvider.PickSourceAsync().ConfigureAwait(true);
            if (source is null)
            {
                StatusMessage = "Picker cancelled.";
                return;
            }
            await StartSharingAsync(source).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Share failed: {ex.Message}";
            await StopSharingInternalAsync().ConfigureAwait(true);
        }
    }

    private async Task StartSharingAsync(ICaptureSource source)
    {
        LocalStreamLabel = source.DisplayName;
        _localCaptureSource = source;
        source.Closed += OnLocalCaptureClosed;

        var session = _webRtc!;
        var streamer = new CaptureStreamer(
            source,
            (duration, payload) => session.PeerConnection.SendVideo(duration, payload),
            _settings.Video,
            _encoderFactory);
        _captureStreamer = streamer;
        streamer.Start();

        await source.StartAsync().ConfigureAwait(true);
        IsSharing = true;
        StatusMessage = null;

        var streamId = Guid.NewGuid().ToString("N");
        _localStreamId = streamId;
        try
        {
            await _signaling.SendStreamStartedAsync(streamId, StreamKind.Screen, hasAudio: false)
                .ConfigureAwait(true);
            DebugLog.Write($"[room] sent StreamStarted (streamId={streamId})");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[room] SendStreamStartedAsync threw: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task StopSharingAsync() => StopSharingInternalAsync();

    [RelayCommand]
    private void ShowSettings()
    {
        var settingsVm = new SettingsViewModel(
            _settings,
            _settingsStore,
            _navigation,
            () => this,
            onSaved: () => _ = RebuildMediaGraphAsync());
        _navigation.NavigateTo(settingsVm);
    }

    public void OnRoomIdCopied()
    {
        CopyButtonText = "Copied!";
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1.5),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyButtonText = "Copy";
        };
        timer.Start();
    }

    public async Task RebuildMediaGraphAsync()
    {
        if (_settings.Video.Codec == _sessionCodec)
        {
            DebugLog.Write($"[room] RebuildMediaGraphAsync no-op — codec unchanged ({_sessionCodec}); fps/bitrate/resolution apply on next Share");
            return;
        }

        DebugLog.Write($"[room] RebuildMediaGraphAsync — codec switch {_sessionCodec} -> {_settings.Video.Codec}");
        MediaStatus = "Switching codec...";

        await StopSharingInternalAsync().ConfigureAwait(true);

        ClearRemoteStream("Switching codec...");
        if (_streamReceiver is not null)
        {
            try { _streamReceiver.FrameArrived -= OnRemoteFrameArrived; } catch { }
            try { await _streamReceiver.DisposeAsync().ConfigureAwait(true); } catch { }
            _streamReceiver = null;
            OnPropertyChanged(nameof(StreamReceiver));
        }
        if (_webRtc is not null)
        {
            try { await _webRtc.DisposeAsync().ConfigureAwait(true); } catch { }
            _webRtc = null;
        }

        var catalog = ClientHost.VideoCodecCatalog ?? new VideoCodecCatalog();
        var resolved = catalog.ResolveOrFallback(_settings.Video.Codec);
        _sessionCodec = resolved.selected;
        _encoderFactory = resolved.encoderFactory;
        _decoderFactory = resolved.decoderFactory;

        _negotiationStarted = false;
        _ = StartWebRtcAsync();
    }

    private async Task StopSharingInternalAsync()
    {
        var streamer = _captureStreamer;
        var source = _localCaptureSource;

        _captureStreamer = null;
        _localCaptureSource = null;

        if (streamer is not null)
        {
            streamer.Stop();
            streamer.Dispose();
        }

        if (source is not null)
        {
            source.Closed -= OnLocalCaptureClosed;
            try { await source.StopAsync().ConfigureAwait(true); } catch { }
            try { await source.DisposeAsync().ConfigureAwait(true); } catch { }
        }

        IsSharing = false;
        LocalStreamLabel = null;

        var streamId = _localStreamId;
        _localStreamId = null;
        if (streamId is not null && _signaling.IsConnected)
        {
            try
            {
                await _signaling.SendStreamEndedAsync(streamId).ConfigureAwait(true);
                DebugLog.Write($"[room] sent StreamEnded (streamId={streamId})");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[room] SendStreamEndedAsync threw: {ex.Message}");
            }
        }
    }

    private void OnLocalCaptureClosed()
    {
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            StatusMessage = "Capture source closed.";
            await StopSharingInternalAsync().ConfigureAwait(true);
        }));
    }

    private void OnPeerJoined(PeerInfo peer)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (Peers.Any(p => p.PeerId == peer.PeerId)) return;
            Peers.Add(new PeerViewModel(peer.PeerId, peer.DisplayName, peer.IsHost, peer.PeerId == YourPeerId));
        }));
    }

    private void OnPeerLeft(Guid peerId, Guid? newHostPeerId)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            var existing = Peers.FirstOrDefault(p => p.PeerId == peerId);
            if (existing is not null) Peers.Remove(existing);

            if (_currentlyStreamingPeerId == peerId)
            {
                ClearRemoteStream("Stream ended");
            }

            if (newHostPeerId.HasValue)
            {
                HostPeerId = newHostPeerId;
                foreach (var p in Peers) p.IsHost = p.PeerId == newHostPeerId.Value;
                UpdateYouAreHost();
                if (YouAreHost) StatusMessage = "Previous host left. You are now the host.";
            }
        }));
    }

    private void OnStreamStartedReceived(StreamStarted message)
    {
        DebugLog.Write($"[room] StreamStarted received from {message.PeerId} (self={YourPeerId}, streamId={message.StreamId})");
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (message.PeerId == YourPeerId) return;
            _currentlyStreamingPeerId = message.PeerId;
            _lastRemoteFrameUtc = DateTime.UtcNow;
            MediaStatus = "Waiting for first frame...";
            StartStaleFrameTimer();
        }));
    }

    private void OnStreamEndedReceived(StreamEnded message)
    {
        DebugLog.Write($"[room] StreamEnded received from {message.PeerId} (current={_currentlyStreamingPeerId})");
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_currentlyStreamingPeerId != message.PeerId) return;
            ClearRemoteStream("Stream ended");
        }));
    }

    private void ClearRemoteStream(string statusMessage)
    {
        _currentlyStreamingPeerId = null;
        HasRemoteStream = false;
        MediaStatus = statusMessage;
        StopStaleFrameTimer();
    }

    private void StartStaleFrameTimer()
    {
        if (_staleFrameTimer is not null) return;
        _staleFrameTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _staleFrameTimer.Tick += (_, _) => OnStaleFrameTick();
        _staleFrameTimer.Start();
    }

    private void StopStaleFrameTimer()
    {
        _staleFrameTimer?.Stop();
        _staleFrameTimer = null;
    }

    private void OnStaleFrameTick()
    {
        if (_currentlyStreamingPeerId is null) return;
        if (!HasRemoteStream) return;
        if (DateTime.UtcNow - _lastRemoteFrameUtc < StaleFrameTimeout) return;
        ClearRemoteStream("Stream stalled");
    }

    private void StartStatsTimer()
    {
        if (_statsTimer is not null) return;
        _statsPrevTickUtc = DateTime.UtcNow;
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statsTimer.Tick += (_, _) => OnStatsTick();
        _statsTimer.Start();
    }

    private void StopStatsTimer()
    {
        _statsTimer?.Stop();
        _statsTimer = null;
    }

    private void OnStatsTick()
    {
        var now = DateTime.UtcNow;
        var elapsedSeconds = Math.Max(0.001, (now - _statsPrevTickUtc).TotalSeconds);
        _statsPrevTickUtc = now;

        SenderStats = BuildSenderStats(elapsedSeconds);
        ReceiverStats = BuildReceiverStats(elapsedSeconds);
    }

    private string BuildSenderStats(double elapsedSeconds)
    {
        var streamer = _captureStreamer;
        if (streamer is null) return "not sharing";

        var currentFrames = streamer.EncodedFrameCount;
        var currentBytes = streamer.EncodedByteCount;
        var deltaFrames = currentFrames - _statsPrevSenderFrames;
        var deltaBytes = currentBytes - _statsPrevSenderBytes;
        _statsPrevSenderFrames = currentFrames;
        _statsPrevSenderBytes = currentBytes;

        var fps = deltaFrames / elapsedSeconds;
        var mbps = deltaBytes * 8.0 / elapsedSeconds / 1_000_000.0;
        var codec = streamer.CurrentCodec?.ToString() ?? "—";

        return
            $"codec:  {codec}\n" +
            $"src:    {streamer.SourceWidth}x{streamer.SourceHeight}\n" +
            $"enc:    {streamer.EncoderWidth}x{streamer.EncoderHeight}\n" +
            $"fps:    {fps:F1} / {streamer.TargetFps}\n" +
            $"rate:   {mbps:F2} / {streamer.TargetBitrate / 1_000_000.0:F1} Mbps\n" +
            $"frames: {streamer.EncodedFrameCount} ({streamer.FrameCount - streamer.EncodedFrameCount} dropped)";
    }

    private string BuildReceiverStats(double elapsedSeconds)
    {
        var receiver = _streamReceiver;
        if (receiver is null || receiver.FramesDecoded == 0) return "no incoming stream";

        var currentFrames = receiver.FramesDecoded;
        var currentBytes = receiver.EncodedByteCount;
        var deltaFrames = currentFrames - _statsPrevReceiverFrames;
        var deltaBytes = currentBytes - _statsPrevReceiverBytes;
        _statsPrevReceiverFrames = currentFrames;
        _statsPrevReceiverBytes = currentBytes;

        var fps = deltaFrames / elapsedSeconds;
        var mbps = deltaBytes * 8.0 / elapsedSeconds / 1_000_000.0;

        var core =
            $"codec:  {receiver.Codec}\n" +
            $"size:   {receiver.LastWidth}x{receiver.LastHeight}\n" +
            $"fps:    {fps:F1}\n" +
            $"rate:   {mbps:F2} Mbps\n" +
            $"frames: {receiver.FramesDecoded}";

        // The view polls the renderer and stores its "paint fps" string
        // here. When empty, we just show the core block.
        var render = RenderStatsLine;
        if (!string.IsNullOrEmpty(render))
        {
            return core + "\n" + render;
        }
        return core;
    }

    private void OnServerError(ErrorCode code, string message)
    {
        _dispatcher.BeginInvoke(new Action(() => StatusMessage = $"Server error: {message}"));
    }

    private void OnConnectionLost(string? reason)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            StatusMessage = string.IsNullOrEmpty(reason)
                ? "Disconnected."
                : $"Disconnected: {reason}";
        }));
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
        StopStatsTimer();

        await StopSharingInternalAsync().ConfigureAwait(true);

        if (_streamReceiver is not null)
        {
            try { _streamReceiver.FrameArrived -= OnRemoteFrameArrived; } catch { }
            await _streamReceiver.DisposeAsync().ConfigureAwait(true);
            _streamReceiver = null;
            OnPropertyChanged(nameof(StreamReceiver));
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
