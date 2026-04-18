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
using ScreenSharing.Client.Windows.Media.Codecs;
using ScreenSharing.Client.Services;
using ScreenSharing.Desktop.App.Services;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;

namespace ScreenSharing.Desktop.App.ViewModels;

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

    // Remote-tile stall state machine.
    //   Active                                      — frames flowing
    //   no frame for PauseAfter         → Paused    — "Paused" overlay
    //   no frame for PauseAfter + IdleAfter → Idle  — empty state
    // A new frame from any moment flips us straight back to Active.
    private static readonly TimeSpan PauseAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(5);
    private DispatcherTimer? _staleFrameTimer;
    private DateTime _lastRemoteFrameUtc = DateTime.MinValue;

    // Per-stream stats. Two timers would be pointless — one 500 ms
    // DispatcherTimer computes fps/mbps deltas for whichever tile is
    // currently showing its overlay. Previous-value state lives here.
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
        if (_encoderFactory is MediaFoundationH264EncoderFactory mfEnc)
        {
            mfEnc.Scaler = settings.Video.Scaler;
        }

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

        // Seed the per-peer IsStreaming flag from the initial roster so
        // the members strip already shows live dots on anyone who was
        // mid-stream when we joined.
        foreach (var p in initial.Peers)
        {
            var vm = Peers.FirstOrDefault(x => x.PeerId == p.PeerId);
            if (vm is not null) vm.IsStreaming = p.IsStreaming;
        }

        var existingStreamer = initial.Peers.FirstOrDefault(
            p => p.IsStreaming && p.PeerId != initial.YourPeerId);
        if (existingStreamer is not null)
        {
            _currentlyStreamingPeerId = existingStreamer.PeerId;
            _lastRemoteFrameUtc = DateTime.UtcNow;
            StartStaleFrameTimer();
        }

        _ = StartWebRtcAsync();
        StartStatsTimer();
    }

    private void SetPeerStreaming(Guid peerId, bool streaming)
    {
        var vm = Peers.FirstOrDefault(p => p.PeerId == peerId);
        if (vm is not null) vm.IsStreaming = streaming;
    }

    public ObservableCollection<PeerViewModel> Peers { get; }

    /// <summary>
    /// Current remote receiver, or null before negotiation. The room view's
    /// D3DImageVideoRenderer picks this up to subscribe for decoded frames.
    /// </summary>
    public StreamReceiver? StreamReceiver => _streamReceiver;

    /// <summary>
    /// Local capture exposed to a separate, smaller renderer for the
    /// streamer's self-preview. Non-null only while <see cref="IsSharing"/>
    /// is true. The main tile renders the remote stream; this one sits
    /// in a small box on top.
    /// </summary>
    public ICaptureSource? LocalPreviewSource => IsSharing ? _localCaptureSource : null;

    partial void OnIsSharingChanged(bool value)
    {
        OnPropertyChanged(nameof(LocalPreviewSource));
    }

    [ObservableProperty] private string _roomId;
    [ObservableProperty] private string _copyButtonText = "Copy";
    [ObservableProperty] private Guid _yourPeerId;
    [ObservableProperty] private Guid? _hostPeerId;
    [ObservableProperty] private bool _youAreHost;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _mediaStatus;
    [ObservableProperty] private bool _isSharing;
    [ObservableProperty] private bool _hasRemoteStream;

    /// <summary>
    /// True when the remote stream has stalled (no frames for
    /// <see cref="PauseAfter"/>) but we haven't yet given up. The room
    /// view freezes the last frame and overlays a "Paused" badge. If
    /// frames start flowing again we flip back to active; if nothing
    /// arrives by <see cref="PauseAfter"/> + <see cref="IdleAfter"/>,
    /// we clear the tile to the empty state.
    /// </summary>
    [ObservableProperty] private bool _isRemotePaused;

    /// <summary>
    /// Union of <see cref="HasRemoteStream"/> and <see cref="IsRemotePaused"/>.
    /// Drives the remote-tile renderer's Visibility so it goes
    /// Collapsed on Idle — the HwndHost child window otherwise keeps
    /// showing whatever was last in its swap chain back buffer.
    /// </summary>
    public bool HasRemoteVideo => HasRemoteStream || IsRemotePaused;

    partial void OnHasRemoteStreamChanged(bool value) => OnPropertyChanged(nameof(HasRemoteVideo));
    partial void OnIsRemotePausedChanged(bool value) => OnPropertyChanged(nameof(HasRemoteVideo));

    /// <summary>
    /// Connection dot brush. Green while the signaling socket is up,
    /// red once it drops. Consumed as a Brush binding in the top bar.
    /// </summary>
    [ObservableProperty] private System.Windows.Media.Brush _connectionDotBrush =
        System.Windows.Media.Brushes.LimeGreen;

    /// <summary>
    /// Nominal frame rate the current remote streamer announced in its
    /// <see cref="StreamStarted"/> message. The renderer's jitter buffer
    /// + paint pacer use this as their tick clock so the wall-clock
    /// gap between painted frames matches the gap between sent frames.
    /// </summary>
    [ObservableProperty] private int _remoteNominalFrameRate = 60;

    // Right-edge stats panel. Single open/close flag — replaces the
    // per-tile hover overlays which couldn't work under HwndHost
    // airspace. The button on the top bar toggles it; an X inside
    // the panel closes it.
    [ObservableProperty] private bool _isStatsPanelOpen;
    [ObservableProperty] private string _outgoingStats = "—";
    [ObservableProperty] private string _incomingStats = "—";

    /// <summary>
    /// Paint-fps line for the self-preview renderer. The view's 500 ms
    /// poller snapshots the renderer and writes here; the stats timer
    /// appends it to <see cref="OutgoingStats"/>.
    /// </summary>
    [ObservableProperty] private string? _selfRenderStatsLine;

    /// <summary>
    /// Paint-fps line for the remote-stream renderer. Same pattern.
    /// </summary>
    [ObservableProperty] private string? _remoteRenderStatsLine;

    [RelayCommand]
    private void ToggleStatsPanel() => IsStatsPanelOpen = !IsStatsPanelOpen;

    [RelayCommand]
    private void CloseStatsPanel() => IsStatsPanelOpen = false;

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
            // The GPU texture fast path emits on TextureArrived and DOES NOT
            // raise FrameArrived (the decoder skips the CPU readback). We
            // still need to flip HasRemoteStream so the RemoteRenderer is
            // laid out and visible — without this the tile stays Collapsed
            // and "nothing renders" even though the renderer is successfully
            // painting underneath.
            receiver.TextureArrived += OnRemoteTextureArrived;
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
                try { receiver.TextureArrived -= OnRemoteTextureArrived; } catch { }
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
        NotifyRemoteFrameObserved();
    }

    // Same job as OnRemoteFrameArrived but for the GPU-texture fast path.
    // StreamReceiver's TextureArrived carries no CaptureFrameData, only the
    // native pointer + dimensions + timestamp, which we don't need here —
    // we just need a signal that frames are flowing so the remote tile can
    // become visible.
    private void OnRemoteTextureArrived(IntPtr nativeTexture, int width, int height, TimeSpan timestamp)
    {
        NotifyRemoteFrameObserved();
    }

    private void NotifyRemoteFrameObserved()
    {
        // Drop late frames that arrive after we've torn down the stream.
        // StreamEnded comes in on the signaling path (TCP) and a handful
        // of RTP packets can still be in flight on the media path (UDP);
        // without this guard, those late decoded frames would call back
        // into EnterActiveState() and re-flip HasRemoteStream to true
        // forever, leaving the last frame stuck on screen because the
        // stale-frame timer also checks _currentlyStreamingPeerId.
        if (_currentlyStreamingPeerId is null) return;

        _lastRemoteFrameUtc = DateTime.UtcNow;

        // Fast path: already in the Active state with nothing to clear.
        // Skip the dispatcher hop entirely — this fires on every decoded
        // frame, potentially 120+ times per second.
        if (HasRemoteStream && !IsRemotePaused && MediaStatus is null) return;

        if (_dispatcher.CheckAccess())
        {
            EnterActiveState();
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(EnterActiveState));
        }
    }

    private void EnterActiveState()
    {
        HasRemoteStream = true;
        IsRemotePaused = false;
        MediaStatus = null;
    }

    [RelayCommand]
    private async Task ShareScreenAsync()
    {
        if (IsSharing || _captureProvider is null || _webRtc is null) return;

        ICaptureSource? source = null;
        try
        {
            source = await _captureProvider.PickSourceAsync(_settings.Video.TargetFrameRate).ConfigureAwait(true);
            if (source is null)
            {
                StatusMessage = "Picker cancelled.";
                return;
            }

            _localCaptureSource = source;
            SetPeerStreaming(YourPeerId, true);
            source.Closed += OnLocalCaptureClosed;

            var session = _webRtc;
            var streamer = new CaptureStreamer(
                source,
                (duration, payload, _) => session.PeerConnection.SendVideo(duration, payload),
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
                await _signaling.SendStreamStartedAsync(
                    streamId,
                    StreamKind.Screen,
                    hasAudio: false,
                    nominalFrameRate: _settings.Video.TargetFrameRate)
                    .ConfigureAwait(true);
                DebugLog.Write($"[room] sent StreamStarted (streamId={streamId}, nominalFps={_settings.Video.TargetFrameRate})");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[room] SendStreamStartedAsync threw: {ex.Message}");
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
            try { _streamReceiver.TextureArrived -= OnRemoteTextureArrived; } catch { }
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
        if (_encoderFactory is MediaFoundationH264EncoderFactory mfEnc2)
        {
            mfEnc2.Scaler = _settings.Video.Scaler;
        }

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
        SetPeerStreaming(YourPeerId, false);

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
                // PeerLeft with no StreamEnded preceding it = the
                // streamer didn't gracefully stop (process killed,
                // WebSocket dropped, network cut). Treat it like a
                // network stall: freeze on the last frame, show Paused,
                // let the stale-frame timer drop us to Idle after
                // IdleAfter if nothing recovers.
                EnterPausedState();
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
        DebugLog.Write($"[room] StreamStarted received from {message.PeerId} (self={YourPeerId}, streamId={message.StreamId}, nominalFps={message.NominalFrameRate})");
        _dispatcher.BeginInvoke(new Action(() =>
        {
            SetPeerStreaming(message.PeerId, true);
            if (message.PeerId == YourPeerId) return;
            _currentlyStreamingPeerId = message.PeerId;
            _lastRemoteFrameUtc = DateTime.UtcNow;
            // The receiver's paint pacer needs the cadence the sender
            // promised, so it can size its jitter buffer and tick at
            // the same rate. Defaults to 60 if the sender pre-dates
            // this field.
            RemoteNominalFrameRate = message.NominalFrameRate > 0 ? message.NominalFrameRate : 60;
            StartStaleFrameTimer();
        }));
    }

    private void OnStreamEndedReceived(StreamEnded message)
    {
        DebugLog.Write($"[room] StreamEnded received from {message.PeerId} (current={_currentlyStreamingPeerId})");
        _dispatcher.BeginInvoke(new Action(() =>
        {
            SetPeerStreaming(message.PeerId, false);
            if (_currentlyStreamingPeerId != message.PeerId) return;
            // Explicit StreamEnded = the streamer stopped on purpose.
            // Jump straight to Idle so watchers don't stare at a frozen
            // last frame for no reason. Paused is reserved for
            // unexpected stalls (network drop, process crash) caught by
            // the stale-frame timer.
            ClearRemoteStream(null);
        }));
    }

    /// <summary>
    /// Moves the remote tile into Idle — clears the streamer, hides the
    /// last frame, stops the stall timer. Called on explicit teardown
    /// paths (LeaveRoom, codec switch) and from the stall state machine
    /// once we've sat in Paused long enough.
    /// </summary>
    private void ClearRemoteStream(string? statusMessage)
    {
        if (_currentlyStreamingPeerId is Guid id) SetPeerStreaming(id, false);
        _currentlyStreamingPeerId = null;
        HasRemoteStream = false;
        IsRemotePaused = false;
        MediaStatus = statusMessage;
        StopStaleFrameTimer();
    }

    /// <summary>
    /// Moves the remote tile into Paused — keeps the last frame on
    /// screen and shows the "Paused" overlay, but leaves the stall
    /// timer running so we can either recover to Active or drop to
    /// Idle. No-op if we're not currently receiving a stream.
    /// </summary>
    private void EnterPausedState()
    {
        if (!HasRemoteStream && !IsRemotePaused) return;
        HasRemoteStream = false;
        IsRemotePaused = true;
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

        var gap = DateTime.UtcNow - _lastRemoteFrameUtc;

        // Active → Paused: freeze the last frame and show a Paused badge.
        if (HasRemoteStream && gap >= PauseAfter)
        {
            EnterPausedState();
            return;
        }

        // Paused → Idle: give up, clear the tile to the empty state.
        if (IsRemotePaused && gap >= PauseAfter + IdleAfter)
        {
            ClearRemoteStream(null);
        }
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

        OutgoingStats = BuildOutgoingStats(elapsedSeconds);
        IncomingStats = BuildIncomingStats(elapsedSeconds);
    }

    private string BuildOutgoingStats(double elapsedSeconds)
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

        var core =
            $"codec:  {codec}\n" +
            $"src:    {streamer.SourceWidth}x{streamer.SourceHeight}\n" +
            $"enc:    {streamer.EncoderWidth}x{streamer.EncoderHeight}\n" +
            $"fps:    {fps:F1} / {streamer.TargetFps}\n" +
            $"rate:   {mbps:F2} / {streamer.TargetBitrate / 1_000_000.0:F1} Mbps\n" +
            $"frames: {streamer.EncodedFrameCount} ({streamer.FrameCount - streamer.EncodedFrameCount} dropped)";

        return string.IsNullOrEmpty(SelfRenderStatsLine)
            ? core
            : core + "\n" + SelfRenderStatsLine;
    }

    private string BuildIncomingStats(double elapsedSeconds)
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

        return string.IsNullOrEmpty(RemoteRenderStatsLine)
            ? core
            : core + "\n" + RemoteRenderStatsLine;
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
            ConnectionDotBrush = System.Windows.Media.Brushes.OrangeRed;
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
            try { _streamReceiver.TextureArrived -= OnRemoteTextureArrived; } catch { }
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
