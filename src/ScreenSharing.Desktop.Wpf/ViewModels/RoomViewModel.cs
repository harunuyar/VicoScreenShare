using System;
using System.Collections.Generic;
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
    // Not readonly: on reconnect we swap _signaling for a brand-new SignalingClient
    // (the old one is one-shot and disposed). All event subscriptions re-wire
    // onto the new instance in ResubscribeSignalingEvents.
    private SignalingClient _signaling;
    private readonly INavigationHost _navigation;
    private readonly IdentityStore _identity;
    private readonly Func<SignalingClient> _signalingFactory;
    private readonly ClientSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ICaptureProvider? _captureProvider;
    private readonly Dispatcher _dispatcher;

    private WebRtcSession? _webRtc;
    private ICaptureSource? _localCaptureSource;
    private CaptureStreamer? _captureStreamer;
    private bool _negotiationStarted;

    private string _resumeToken = string.Empty;
    private TimeSpan _resumeTtl = TimeSpan.Zero;
    private CancellationTokenSource? _reconnectCts;
    private bool _leavingIntentionally;

    // True iff this client was publishing at the instant of the WS drop. The
    // reconnect loop uses this to decide whether to restart the capture
    // pipeline against the freshly-negotiated main PC and re-emit
    // StreamStarted with the stashed StreamId so viewers keep their tile.
    private bool _wasSharingBeforeDisconnect;
    private string? _stashedStreamIdForResume;

    private VideoCodec _sessionCodec;
    private IVideoEncoderFactory _encoderFactory;
    private IVideoDecoderFactory _decoderFactory;

    // Per-publisher tile. Each wraps a SubscriberSession (the client RecvOnly
    // PC + decoder) and owns its own stall state + stats. The tile collection
    // drives the ItemsControl grid in RoomView.xaml; adding/removing a tile
    // mounts/unmounts its D3DImageVideoRenderer.
    public ObservableCollection<PublisherTileViewModel> Tiles { get; } = new();

    // Nominal frame rate per publisher, captured from StreamStarted so the
    // per-tile renderer's jitter buffer can size to the sender's cadence even
    // before the subscriber PC negotiation completes. Keyed by publisher peer id.
    private readonly Dictionary<Guid, int> _announcedFrameRates = new();

    private string? _localStreamId;

    // Per-stream sender stats. Receiver-side stats are per-tile now.
    private DispatcherTimer? _statsTimer;
    private long _statsPrevSenderFrames;
    private long _statsPrevSenderBytes;
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
        _resumeToken = initial.ResumeToken ?? string.Empty;
        _resumeTtl = initial.ResumeTtl;

        Peers = new ObservableCollection<PeerViewModel>(
            initial.Peers.Select(p => new PeerViewModel(
                p.PeerId,
                p.DisplayName,
                p.IsHost,
                p.PeerId == initial.YourPeerId)));

        HostPeerId = Peers.FirstOrDefault(p => p.IsHost)?.PeerId;
        UpdateYouAreHost();

        Tiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAnyTile));
            OnPropertyChanged(nameof(TileCount));
        };

        SubscribeSignalingEvents(_signaling);

        // Seed the per-peer IsStreaming flag from the initial roster so
        // the members strip already shows live dots on anyone who was
        // mid-stream when we joined.
        foreach (var p in initial.Peers)
        {
            var vm = Peers.FirstOrDefault(x => x.PeerId == p.PeerId);
            if (vm is not null) vm.IsStreaming = p.IsStreaming;
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

    /// <summary>True while at least one publisher tile is mounted.</summary>
    public bool HasAnyTile => Tiles.Count > 0;

    public int TileCount => Tiles.Count;

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

    /// <summary>
    /// Overlay text shown when no tiles are mounted yet (e.g. "Waiting for a
    /// streamer…", "Switching codec…", "Media setup failed: …"). When a tile
    /// mounts, it overlays the grid and this status goes dormant.
    /// </summary>
    [ObservableProperty] private string? _mediaStatus;
    [ObservableProperty] private bool _isSharing;

    /// <summary>
    /// Connection dot brush. Green while the signaling socket is up,
    /// red once it drops. Consumed as a Brush binding in the top bar.
    /// </summary>
    [ObservableProperty] private System.Windows.Media.Brush _connectionDotBrush =
        System.Windows.Media.Brushes.LimeGreen;

    /// <summary>
    /// True while the signaling WebSocket is down and <see cref="ReconnectLoopAsync"/>
    /// is actively trying to bring it back. The room view shows a "Reconnecting…"
    /// chip and keeps the user on the room rather than navigating home.
    /// </summary>
    [ObservableProperty] private bool _isReconnecting;

    /// <summary>
    /// Human-readable label for the reconnect chip. "Reconnecting…" during the
    /// grace window, a terminal message if the resume fails.
    /// </summary>
    [ObservableProperty] private string _reconnectMessage = "Reconnecting…";

    // Right-edge stats panel. Single open/close flag — drives the panel
    // on the view; when open, the panel aggregates self-outgoing stats
    // plus per-tile incoming stats.
    [ObservableProperty] private bool _isStatsPanelOpen;
    [ObservableProperty] private string _outgoingStats = "—";

    /// <summary>
    /// Combined incoming-stats string — concatenates each tile's stats.
    /// Phase 6 may replace this with per-tile overlays, but for now the
    /// stats panel shows a single pane for all active tiles.
    /// </summary>
    [ObservableProperty] private string _incomingStats = "—";

    /// <summary>
    /// Paint-fps line for the self-preview renderer. The view's 500 ms
    /// poller snapshots the renderer and writes here; the stats timer
    /// appends it to <see cref="OutgoingStats"/>.
    /// </summary>
    [ObservableProperty] private string? _selfRenderStatsLine;

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
        try
        {
            // Main PC is now send-only in practice: the client-initiated
            // negotiation exists so the peer connection is warm and ready to
            // carry the user's own outbound screen share when they click Share.
            // Fan-in (remote streams) flows through per-publisher
            // SubscriberSession PCs, created reactively on server-driven
            // SdpOffers tagged with a publisher's SubscriptionId.
            session = new WebRtcSession(_signaling, WebRtcRole.Bidirectional, _sessionCodec);

            await session.NegotiateAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);

            _webRtc = session;
            MediaStatus = "Waiting for a streamer...";
        }
        catch (Exception ex)
        {
            MediaStatus = $"Media setup failed: {ex.Message}";
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(true); } catch { }
            }
            _negotiationStarted = false;
        }
    }

    /// <summary>
    /// Handle a server-driven SDP offer. Null SubscriptionId = main PC offer
    /// (unused — the client is the offerer on that PC). Non-null = a subscriber
    /// PC the server is driving for a specific publisher; we build a fresh
    /// RecvOnly PC for it, answer, and wire up its StreamReceiver.
    /// </summary>
    private void OnSdpOfferReceived(SdpOffer offer)
    {
        if (string.IsNullOrEmpty(offer.SubscriptionId)) return;
        if (!Guid.TryParseExact(offer.SubscriptionId, "N", out var publisherPeerId)) return;

        // Fire-and-forget: setRemoteDescription / createAnswer / send happens on
        // a background task; UI state transitions are dispatched back.
        _ = CreateOrReplaceSubscriberAsync(publisherPeerId, offer.Sdp);
    }

    private async Task CreateOrReplaceSubscriberAsync(Guid publisherPeerId, string offerSdp)
    {
        // Capture the publisher's display-name and announced cadence while the
        // subscriber is wired up; defaults if neither is known yet (the peer may
        // have left the roster after sending the offer, or StreamStarted arrived
        // after the SdpOffer on the signaling stream).
        var existingPeer = Peers.FirstOrDefault(p => p.PeerId == publisherPeerId);
        var displayName = existingPeer?.DisplayName ?? $"Publisher {publisherPeerId:N}".Substring(0, 16);
        var nominalFps = _announcedFrameRates.TryGetValue(publisherPeerId, out var fps) ? fps : 60;

        PublisherTileViewModel? toDispose = null;
        lock (Tiles)
        {
            // If a tile already exists for this publisher, we're replacing its
            // underlying PC (re-offer path — Phase 5 uses this on publisher
            // reconnect). Take the old one out and dispose it after we've swapped
            // in the replacement so the grid doesn't flicker.
            var existing = Tiles.FirstOrDefault(t => t.PublisherPeerId == publisherPeerId);
            if (existing is not null)
            {
                toDispose = existing;
            }
        }

        var session = new SubscriberSession(_signaling, publisherPeerId, _decoderFactory,
            displayName: displayName);

        try
        {
            await session.AcceptOfferAsync(offerSdp).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[room] subscriber offer for {publisherPeerId:N} failed: {ex.Message}");
            try { await session.DisposeAsync().ConfigureAwait(true); } catch { }
            return;
        }

        var tile = new PublisherTileViewModel(session, displayName, nominalFps);

        await _dispatcher.InvokeAsync(() =>
        {
            if (toDispose is not null)
            {
                Tiles.Remove(toDispose);
            }
            Tiles.Add(tile);
            // If the media status was "Waiting for a streamer…" etc, clear it so
            // the grid takes over the empty state region.
            MediaStatus = null;
        }).Task.ConfigureAwait(true);

        if (toDispose is not null)
        {
            try { await toDispose.DisposeAsync().ConfigureAwait(true); } catch { }
        }

        DebugLog.Write($"[room] mounted tile for {publisherPeerId:N} ({displayName})");
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

        MediaStatus = "Switching codec...";
        await DisposeAllSubscribersAsync().ConfigureAwait(true);
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
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            var existing = Peers.FirstOrDefault(p => p.PeerId == peerId);
            if (existing is not null) Peers.Remove(existing);

            // Dispose the tile (and its subscription) for this peer if any.
            await DisposeTileAsync(peerId).ConfigureAwait(true);

            _announcedFrameRates.Remove(peerId);

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
            var fps = message.NominalFrameRate > 0 ? message.NominalFrameRate : 60;
            _announcedFrameRates[message.PeerId] = fps;

            // If a tile is already mounted (race between StreamStarted and the
            // subscriber SDP offer — StreamStarted can arrive after the SdpOffer),
            // push the cadence into it now.
            var existingTile = Tiles.FirstOrDefault(t => t.PublisherPeerId == message.PeerId);
            if (existingTile is not null)
            {
                existingTile.NominalFrameRate = fps;
            }
        }));
    }

    private void OnStreamEndedReceived(StreamEnded message)
    {
        DebugLog.Write($"[room] StreamEnded received from {message.PeerId}");
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            SetPeerStreaming(message.PeerId, false);
            _announcedFrameRates.Remove(message.PeerId);

            // Drop the subscription + tile for this publisher. Server has
            // already torn down its matching SfuSubscriberPeer on its side.
            await DisposeTileAsync(message.PeerId).ConfigureAwait(true);
        }));
    }

    /// <summary>Dispose a single tile for the given publisher, if one exists.</summary>
    private async Task DisposeTileAsync(Guid publisherPeerId)
    {
        PublisherTileViewModel? tile;
        tile = Tiles.FirstOrDefault(t => t.PublisherPeerId == publisherPeerId);
        if (tile is null) return;

        Tiles.Remove(tile);
        try { await tile.DisposeAsync().ConfigureAwait(true); } catch { }
    }

    private async Task DisposeAllSubscribersAsync()
    {
        var all = Tiles.ToArray();
        Tiles.Clear();
        foreach (var tile in all)
        {
            try { await tile.DisposeAsync().ConfigureAwait(true); } catch { }
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
        if (Tiles.Count == 0) return "no incoming stream";

        var sb = new System.Text.StringBuilder();
        foreach (var tile in Tiles)
        {
            tile.UpdateStats(elapsedSeconds);
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(tile.DisplayName).Append(":\n").Append(tile.Stats);
        }
        return sb.ToString();
    }

    private void OnServerError(ErrorCode code, string message)
    {
        _dispatcher.BeginInvoke(new Action(() => StatusMessage = $"Server error: {message}"));
    }

    private void OnConnectionLost(string? reason)
    {
        // Don't kick off a reconnect if the user is on their way out.
        if (_leavingIntentionally) return;

        _dispatcher.BeginInvoke(new Action(async () =>
        {
            StatusMessage = null;
            ConnectionDotBrush = System.Windows.Media.Brushes.OrangeRed;
            IsReconnecting = true;
            ReconnectMessage = "Reconnecting…";

            // If we were publishing at the drop, stash the stream id so the
            // reconnect path can re-emit StreamStarted with the same id and
            // viewers keep their tile instead of unmounting + remounting.
            _wasSharingBeforeDisconnect = IsSharing;
            _stashedStreamIdForResume = _localStreamId;

            // Pause the encoder pipeline — it was shipping bytes to a PC that
            // no longer exists. The capture source stays running so the
            // self-preview keeps painting; we rebuild the encoder + rewire it
            // to the new PC on successful resume.
            if (_captureStreamer is not null)
            {
                try { _captureStreamer.Stop(); } catch { }
                try { _captureStreamer.Dispose(); } catch { }
                _captureStreamer = null;
            }

            // Tear down local media state tied to the dead signaling client.
            // The server side is equivalent: our SfuPeer + our subscriber PCs
            // are disposed on entry to the server grace window. A successful
            // resume rebuilds both sides from scratch.
            await DisposeAllSubscribersAsync().ConfigureAwait(true);
            if (_webRtc is not null)
            {
                try { await _webRtc.DisposeAsync().ConfigureAwait(true); } catch { }
                _webRtc = null;
            }
            _negotiationStarted = false;

            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();
            _ = ReconnectLoopAsync(_reconnectCts.Token);
        }));
    }

    private void SubscribeSignalingEvents(SignalingClient client)
    {
        client.PeerJoined += OnPeerJoined;
        client.PeerLeft += OnPeerLeft;
        client.ServerError += OnServerError;
        client.ConnectionLost += OnConnectionLost;
        client.StreamStartedReceived += OnStreamStartedReceived;
        client.StreamEndedReceived += OnStreamEndedReceived;
        client.SdpOfferReceived += OnSdpOfferReceived;
        client.PeerConnectionStateChanged += OnPeerConnectionStateChanged;
        client.ResumeFailedReceived += OnResumeFailedReceived;
    }

    private void UnsubscribeSignalingEvents(SignalingClient client)
    {
        client.PeerJoined -= OnPeerJoined;
        client.PeerLeft -= OnPeerLeft;
        client.ServerError -= OnServerError;
        client.ConnectionLost -= OnConnectionLost;
        client.StreamStartedReceived -= OnStreamStartedReceived;
        client.StreamEndedReceived -= OnStreamEndedReceived;
        client.SdpOfferReceived -= OnSdpOfferReceived;
        client.PeerConnectionStateChanged -= OnPeerConnectionStateChanged;
        client.ResumeFailedReceived -= OnResumeFailedReceived;
    }

    private void OnPeerConnectionStateChanged(PeerConnectionState state)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            var peer = Peers.FirstOrDefault(p => p.PeerId == state.PeerId);
            if (peer is null) return;
            peer.IsConnected = state.IsConnected;
        }));
    }

    private void OnResumeFailedReceived(ResumeFailed failure)
    {
        // The server rejected our resume attempt. No point in retrying further —
        // the slot is gone. Signal the reconnect loop to stop and navigate home.
        _dispatcher.BeginInvoke(new Action(() =>
        {
            ReconnectMessage = failure.Reason switch
            {
                ResumeFailedReason.Expired => "Reconnect timed out. Returning to home…",
                ResumeFailedReason.RoomGone => "Room closed. Returning to home…",
                _ => "Session unrecognized. Returning to home…",
            };
            _reconnectCts?.Cancel();
            // Delay the nav-away slightly so the message is readable.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(NavigateHome);
            });
        }));
    }

    /// <summary>
    /// Reconnect loop with capped exponential backoff. On each attempt we build
    /// a brand-new <see cref="SignalingClient"/> (the old one is one-shot), send
    /// <c>ClientHello</c> + <c>ResumeSession</c>, and swap it into place on
    /// success. Gives up silently when <paramref name="ct"/> fires — either
    /// because we left intentionally or because a <c>ResumeFailed</c> came in.
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + (_resumeTtl > TimeSpan.Zero ? _resumeTtl : TimeSpan.FromSeconds(20));
        var delayMs = 500;
        var rng = new Random();

        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= deadline)
            {
                // Fall out of the loop and navigate away. Server has torn down
                // the slot; presenting a fresh JoinRoom would surface a
                // RoomNotFound in a typical case.
                await _dispatcher.InvokeAsync(() =>
                {
                    ReconnectMessage = "Reconnect timed out. Returning to home…";
                });
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(NavigateHome);
                return;
            }

            SignalingClient? candidate = null;
            try
            {
                candidate = _signalingFactory();
                // Subscribe events BEFORE connect — catches an early ConnectionLost
                // emitted during the hello/resume handshake.
                SubscribeSignalingEvents(candidate);

                var profile = _identity.LoadOrCreate();
                var hello = new ClientHello(profile.UserId, profile.DisplayName, ProtocolVersion.Current);
                await candidate.ConnectAsync(_settings.ServerUri, hello, ct).ConfigureAwait(false);

                // Ask the server to rebind us to our existing room slot.
                await candidate.ResumeSessionAsync(RoomId, _resumeToken, ct).ConfigureAwait(false);

                // Wait for either RoomJoined (success) or ResumeFailed (route via event).
                var tcs = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnResumedJoined(RoomJoined rj) => tcs.TrySetResult(rj);
                candidate.RoomJoined += OnResumedJoined;

                try
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5), waitCts.Token))
                        .ConfigureAwait(false);
                    if (completed != tcs.Task)
                    {
                        throw new TimeoutException("Server did not respond to ResumeSession within 5s");
                    }

                    var rj = await tcs.Task.ConfigureAwait(false);

                    // Swap the signaling client under the dispatcher so event
                    // handlers see the new instance first.
                    await _dispatcher.InvokeAsync(async () =>
                    {
                        var old = _signaling;
                        UnsubscribeSignalingEvents(old);
                        _signaling = candidate;
                        candidate = null; // ownership transferred
                        try { await old.DisposeAsync().ConfigureAwait(true); } catch { }

                        _resumeToken = rj.ResumeToken ?? _resumeToken;
                        _resumeTtl = rj.ResumeTtl != default ? rj.ResumeTtl : _resumeTtl;

                        // Merge the fresh roster snapshot into Peers. Any new
                        // peers the server knows about get added; IsConnected
                        // flags sync back to the server's authoritative view.
                        ApplyRosterSnapshot(rj.Peers);

                        // Rebuild the main PC, then — if we were publishing at
                        // the drop — restart the capture streamer against it
                        // and re-announce StreamStarted with the original
                        // StreamId so viewers keep their tile.
                        _ = StartWebRtcAsync().ContinueWith(async _ =>
                        {
                            if (_wasSharingBeforeDisconnect && _localCaptureSource is not null && _webRtc is not null)
                            {
                                await _dispatcher.InvokeAsync(() => RestoreSharingAfterResumeAsync()).Task.ConfigureAwait(false);
                            }
                            _wasSharingBeforeDisconnect = false;
                            _stashedStreamIdForResume = null;
                        });

                        IsReconnecting = false;
                        ConnectionDotBrush = System.Windows.Media.Brushes.LimeGreen;
                        StatusMessage = null;
                    }).Task.ConfigureAwait(false);

                    return;
                }
                finally
                {
                    candidate?.RoomJoined -= OnResumedJoined;
                }
            }
            catch (OperationCanceledException)
            {
                if (candidate is not null)
                {
                    UnsubscribeSignalingEvents(candidate);
                    try { await candidate.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                return;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[room] reconnect attempt failed: {ex.Message}");
                if (candidate is not null)
                {
                    UnsubscribeSignalingEvents(candidate);
                    try { await candidate.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }

            // Capped exponential backoff with a ±20% jitter so a room full of
            // simultaneously-dropped clients don't all hammer on the same ticks.
            var jitter = 1.0 + (rng.NextDouble() * 0.4 - 0.2);
            var wait = Math.Min(10_000, (int)(delayMs * jitter));
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delayMs = Math.Min(10_000, delayMs * 2);
        }
    }

    private void ApplyRosterSnapshot(IReadOnlyList<PeerInfo> snapshot)
    {
        // Add any peers we don't already know about, update existing ones.
        foreach (var info in snapshot)
        {
            var vm = Peers.FirstOrDefault(p => p.PeerId == info.PeerId);
            if (vm is null)
            {
                Peers.Add(new PeerViewModel(info.PeerId, info.DisplayName, info.IsHost, info.PeerId == YourPeerId)
                {
                    IsStreaming = info.IsStreaming,
                    IsConnected = info.IsConnected,
                });
            }
            else
            {
                vm.DisplayName = info.DisplayName;
                vm.IsHost = info.IsHost;
                vm.IsStreaming = info.IsStreaming;
                vm.IsConnected = info.IsConnected;
            }
        }

        // Drop any peers the server no longer knows about.
        for (var i = Peers.Count - 1; i >= 0; i--)
        {
            if (snapshot.All(s => s.PeerId != Peers[i].PeerId))
            {
                Peers.RemoveAt(i);
            }
        }

        HostPeerId = Peers.FirstOrDefault(p => p.IsHost)?.PeerId;
        UpdateYouAreHost();
    }

    /// <summary>
    /// Rebuild the <see cref="CaptureStreamer"/> against the freshly-negotiated
    /// main PC and re-emit <see cref="StreamStarted"/> with the preserved
    /// StreamId so viewers don't unmount + remount their tile. The capture
    /// source kept running during the drop, so self-preview never flickered.
    /// </summary>
    private async Task RestoreSharingAfterResumeAsync()
    {
        if (_webRtc is null || _localCaptureSource is null) return;

        try
        {
            var session = _webRtc;
            var streamer = new CaptureStreamer(
                _localCaptureSource,
                (duration, payload, _) => session.PeerConnection.SendVideo(duration, payload),
                _settings.Video,
                _encoderFactory);
            _captureStreamer = streamer;
            streamer.Start();

            // Reuse the original stream id so the server's fan-out semantics
            // (viewers already have a tile for this PeerId) keep working across
            // the drop. Without this the viewers would see StreamEnded at the
            // grace boundary, but because we resume first the server never
            // emits one.
            var streamId = _stashedStreamIdForResume ?? Guid.NewGuid().ToString("N");
            _localStreamId = streamId;

            await _signaling.SendStreamStartedAsync(
                streamId,
                StreamKind.Screen,
                hasAudio: false,
                nominalFrameRate: _settings.Video.TargetFrameRate)
                .ConfigureAwait(true);

            DebugLog.Write($"[room] re-emitted StreamStarted after resume (streamId={streamId})");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[room] failed to restore sharing after resume: {ex.Message}");
            StatusMessage = $"Failed to resume share: {ex.Message}";
            await StopSharingInternalAsync().ConfigureAwait(true);
        }
    }

    private void NavigateHome()
    {
        _leavingIntentionally = true;
        _reconnectCts?.Cancel();
        var home = new HomeViewModel(_identity, _signalingFactory, _navigation, _settings, _settingsStore, _captureProvider);
        _navigation.NavigateTo(home);
    }

    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        // Short-circuit the reconnect loop so a WS drop racing with Leave
        // doesn't spin up a resume attempt after we've asked to go home.
        _leavingIntentionally = true;
        _reconnectCts?.Cancel();

        UnsubscribeSignalingEvents(_signaling);
        StopStatsTimer();

        await StopSharingInternalAsync().ConfigureAwait(true);

        // Signal intentional departure so the server bypasses the reconnect
        // grace window and the other peers don't see us as "disconnected" for
        // 20 seconds before the PeerLeft actually arrives.
        if (_signaling.IsConnected)
        {
            try { await _signaling.LeaveRoomAsync().ConfigureAwait(true); } catch { }
        }

        await DisposeAllSubscribersAsync().ConfigureAwait(true);
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
