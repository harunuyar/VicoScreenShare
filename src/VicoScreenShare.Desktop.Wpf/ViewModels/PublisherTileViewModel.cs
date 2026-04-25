namespace VicoScreenShare.Desktop.App.ViewModels;

using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Services;
using VicoScreenShare.Desktop.App.Services;

/// <summary>
/// One tile in the Room's publisher grid. Wraps a <see cref="SubscriberSession"/>
/// (the per-publisher RecvOnly WebRTC PC) and owns its own per-tile state.
///
/// Tile state is driven by the transport layer, NOT by frame arrival. A
/// publisher legitimately stops sending video when its captured content
/// doesn't change (a still editor, a paused video) — the receiver must keep
/// painting the last decoded frame indefinitely while the connection is
/// healthy. The tile flips into a non-Live state only when:
/// <list type="bullet">
/// <item>ICE goes <c>disconnected</c> → <see cref="IsReconnecting"/> (transient).</item>
/// <item>The server signals the publisher's WebSocket dropped (<see cref="PeerViewModel.IsConnected"/>=false) → <see cref="IsReconnecting"/>.</item>
/// <item>ICE goes <c>failed</c> or <c>closed</c> → <see cref="IsLost"/> (terminal). The tile is normally unmounted by <c>RoomViewModel</c> on <c>PeerLeft</c> first; this is the fallback when ICE dies before signaling.</item>
/// </list>
/// </summary>
public sealed partial class PublisherTileViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SubscriberSession _session;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly PeerViewModel? _peer;
    private readonly FrameArrivedHandler _frameHandler;
    private readonly TextureArrivedHandler _textureHandler;
    private readonly Action _onDisconnected;
    private readonly Action _onReconnected;
    private readonly Action _onClosed;
    private readonly PropertyChangedEventHandler? _onPeerPropertyChanged;

    private long _statsPrevFrames;
    private long _statsPrevBytes;
    private DateTime _statsPrevTickUtc = DateTime.MinValue;
    private bool _disposed;

    public PublisherTileViewModel(
        SubscriberSession session,
        string displayName,
        int nominalFrameRate,
        IUiDispatcher uiDispatcher,
        PeerViewModel? peer,
        double initialVolume = 1.0,
        bool initialMuted = false)
    {
        _session = session;
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _peer = peer;
        _displayName = displayName;
        _nominalFrameRate = nominalFrameRate > 0 ? nominalFrameRate : 60;
        _volume = Math.Clamp(initialVolume, 0.0, 1.0);
        _isMuted = initialMuted;

        _frameHandler = OnFrameArrived;
        _textureHandler = OnTextureArrived;
        _session.Receiver.FrameArrived += _frameHandler;
        _session.Receiver.TextureArrived += _textureHandler;

        // Transport-state events from SIPSorcery's RTCPeerConnection,
        // surfaced through StreamReceiver. ICE 'disconnected' is transient
        // (recoverable); 'failed'/'closed' is terminal.
        _onDisconnected = () => _uiDispatcher.Post(() => RecomputeReconnecting(transportDisconnected: true));
        _onReconnected = () => _uiDispatcher.Post(() => RecomputeReconnecting(transportDisconnected: false));
        _onClosed = () => _uiDispatcher.Post(() => IsLost = true);
        _session.Receiver.Disconnected += _onDisconnected;
        _session.Receiver.Reconnected += _onReconnected;
        _session.Receiver.Closed += _onClosed;

        // Signaling-level liveness: the server tells us when the publisher's
        // WebSocket dropped (grace window before PeerLeft). Already mirrored
        // onto PeerViewModel.IsConnected by RoomViewModel — we just observe.
        if (_peer is not null)
        {
            _onPeerPropertyChanged = (_, args) =>
            {
                if (args.PropertyName == nameof(PeerViewModel.IsConnected))
                {
                    _uiDispatcher.Post(() => RecomputeReconnecting(transportDisconnected: _transportDisconnected));
                }
            };
            _peer.PropertyChanged += _onPeerPropertyChanged;
        }

        // Propagate the initial volume/mute into the audio receiver so
        // the first decoded frame already plays at the right level. A
        // null AudioReceiver means this host has no audio backend —
        // the slider still exists in the UI but is cosmetic.
        var audio = _session.AudioReceiver;
        if (audio is not null)
        {
            audio.Volume = _volume;
            audio.IsMuted = _isMuted;
        }
    }

    public Guid PublisherPeerId => _session.PublisherPeerId;

    /// <summary>The <see cref="StreamReceiver"/> feeding decoded frames to the tile's D3D renderer.</summary>
    public StreamReceiver Receiver => _session.Receiver;

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private int _nominalFrameRate;

    /// <summary>
    /// True when this tile is the <see cref="RoomViewModel.FocusedPublisherPeerId"/>.
    /// The Room view uses this to hide the tile from the Focus-layout bottom
    /// strip (where it would duplicate the main focused slot).
    /// </summary>
    [ObservableProperty] private bool _isFocused;

    /// <summary>
    /// True once at least one frame has decoded. Before this, the tile shows a
    /// "Connecting…" placeholder instead of a black rectangle. Once true, it
    /// stays true for the lifetime of the tile — the renderer keeps painting
    /// the last decoded frame regardless of new arrivals.
    /// </summary>
    [ObservableProperty] private bool _hasFirstFrame;

    /// <summary>
    /// Transient connectivity blip — ICE disconnected or the publisher's
    /// WebSocket is in its server-side grace window. Last frame stays
    /// painted; UI overlays a "Reconnecting…" badge.
    /// </summary>
    [ObservableProperty] private bool _isReconnecting;

    /// <summary>
    /// Terminal connection failure — ICE went <c>failed</c> or <c>closed</c>
    /// without a graceful <c>PeerLeft</c>. Last frame stays painted; UI
    /// overlays a "Connection lost" badge. RoomViewModel typically unmounts
    /// the tile shortly after via <c>PeerLeft</c>.
    /// </summary>
    [ObservableProperty] private bool _isLost;

    /// <summary>Stats line for this tile's incoming stream. Updated on each Tick().</summary>
    [ObservableProperty] private string _stats = "—";

    /// <summary>
    /// Linear [0, 1] playback volume for this publisher. Changes flow
    /// straight to the <see cref="AudioReceiver"/> / renderer within
    /// one audio frame. Exposed to the tile UI's volume slider; the
    /// <see cref="RoomViewModel"/> watches
    /// <see cref="PropertyChanged"/> so tile-local changes propagate
    /// to the room's "last used" defaults (new tiles inherit whatever
    /// level the user last picked).
    /// </summary>
    [ObservableProperty] private double _volume = 1.0;

    /// <summary>
    /// When true, incoming audio RTP is dropped at the socket boundary
    /// (no decode, no render), and any already-buffered samples flush
    /// as silence. Separate from <see cref="Volume"/> so a user can
    /// mute-then-unmute without losing their preferred level.
    /// </summary>
    [ObservableProperty] private bool _isMuted;

    /// <summary>Toggle mute for this tile.</summary>
    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    partial void OnVolumeChanged(double value)
    {
        var audio = _session.AudioReceiver;
        if (audio is not null)
        {
            audio.Volume = value;
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        var audio = _session.AudioReceiver;
        if (audio is not null)
        {
            audio.IsMuted = value;
        }
    }

    /// <summary>
    /// Paint fps line pushed in from the view. The view's paint-stats pump writes
    /// this string and the tile appends it to <see cref="Stats"/>.
    /// </summary>
    [ObservableProperty] private string? _renderStatsLine;

    private bool _transportDisconnected;

    private void OnFrameArrived(in CaptureFrameData frame) => NotifyFrameObserved();

    private void OnTextureArrived(IntPtr nativeTexture, int width, int height, TimeSpan timestamp)
        => NotifyFrameObserved();

    private void NotifyFrameObserved()
    {
        // Fast path — already past first frame, nothing to flip.
        if (HasFirstFrame)
        {
            return;
        }

        _uiDispatcher.Post(() => HasFirstFrame = true);
    }

    /// <summary>
    /// Combines the two transient signals (ICE disconnected, signaling-level
    /// peer.IsConnected=false) into the single <see cref="IsReconnecting"/>
    /// flag. Either being unhealthy shows the overlay; both must clear for
    /// the overlay to go away. <see cref="IsLost"/> takes priority — once
    /// terminal, we no longer flip back.
    /// </summary>
    private void RecomputeReconnecting(bool transportDisconnected)
    {
        _transportDisconnected = transportDisconnected;
        if (IsLost)
        {
            return;
        }

        var signalingDown = _peer is not null && !_peer.IsConnected;
        IsReconnecting = transportDisconnected || signalingDown;
    }

    /// <summary>
    /// Called from <see cref="RoomViewModel"/>'s stats timer. Computes per-tile
    /// fps/bitrate deltas and writes to <see cref="Stats"/>.
    /// </summary>
    public void UpdateStats(double elapsedSeconds)
    {
        var r = _session.Receiver;
        if (r.FramesDecoded == 0)
        {
            Stats = "no frames yet";
            return;
        }

        var currentFrames = r.FramesDecoded;
        var currentBytes = r.EncodedByteCount;
        var deltaFrames = currentFrames - _statsPrevFrames;
        var deltaBytes = currentBytes - _statsPrevBytes;
        _statsPrevFrames = currentFrames;
        _statsPrevBytes = currentBytes;

        var fps = deltaFrames / Math.Max(0.001, elapsedSeconds);
        var mbps = deltaBytes * 8.0 / Math.Max(0.001, elapsedSeconds) / 1_000_000.0;

        var core =
            $"codec:  {r.Codec}\n" +
            $"size:   {r.LastWidth}x{r.LastHeight}\n" +
            $"fps:    {fps:F1}\n" +
            $"rate:   {mbps:F2} Mbps\n" +
            $"frames: {r.FramesDecoded}\n" +
            $"loss:   {r.RtpLossPercent:F1}% ({r.RtpPacketsInferredLost} / {r.RtpPacketsReceived + r.RtpPacketsInferredLost})";

        // Decoder-pipeline health. Only shown when non-zero so healthy
        // tiles stay uncluttered — a user who sees "drops: 450" knows
        // their machine is dropping source frames before decode (the
        // classic visible "weird jumps" symptom), and "skip: 3" flags
        // that the drop-to-IDR recovery fired that many times in this
        // session. Both stay hidden at zero.
        var dropped = r.DecodeQueueDroppedCount;
        var skipped = r.SkipToIdrCount;
        var health = (dropped, skipped) switch
        {
            (0, 0) => string.Empty,
            (_, 0) => $"\ndrops:  {dropped}",
            (0, _) => $"\nskip:   {skipped}",
            _ => $"\ndrops:  {dropped}  skip: {skipped}",
        };

        var body = core + health;

        // A/V sync line: only rendered once the MediaClock is locked
        // (publisher SR + anchor latched) AND the tile actually has
        // an AudioReceiver. Positive offset = audio plays late vs
        // the wall clock target (we skip samples to catch up);
        // negative = audio is early (we silence-pad). Steady state
        // should be |offset| < 30 ms (the drift threshold).
        var audio = _session.AudioReceiver;
        if (audio is not null)
        {
            var offsetMicros = audio.LastOffsetMicros;
            if (offsetMicros != 0 || audio.SamplesPadded > 0 || audio.SamplesSkipped > 0)
            {
                var offsetMs = offsetMicros / 1000.0;
                body += $"\nav-sync: {offsetMs:+0.0;-0.0;0.0} ms";
            }
        }

        Stats = string.IsNullOrEmpty(RenderStatsLine) ? body : body + "\n" + RenderStatsLine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try { _session.Receiver.FrameArrived -= _frameHandler; } catch { }
        try { _session.Receiver.TextureArrived -= _textureHandler; } catch { }
        try { _session.Receiver.Disconnected -= _onDisconnected; } catch { }
        try { _session.Receiver.Reconnected -= _onReconnected; } catch { }
        try { _session.Receiver.Closed -= _onClosed; } catch { }
        if (_peer is not null && _onPeerPropertyChanged is not null)
        {
            try { _peer.PropertyChanged -= _onPeerPropertyChanged; } catch { }
        }

        try { await _session.DisposeAsync().ConfigureAwait(false); } catch { }
    }
}
