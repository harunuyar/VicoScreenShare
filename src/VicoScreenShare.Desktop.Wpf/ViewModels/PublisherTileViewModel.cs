namespace VicoScreenShare.Desktop.App.ViewModels;

using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Services;
using VicoScreenShare.Desktop.App.Services;

/// <summary>
/// One tile in the Room's publisher grid. Wraps a <see cref="SubscriberSession"/>
/// (the per-publisher RecvOnly WebRTC PC) and owns its own stall state machine +
/// stats counters, so N publisher tiles don't share state and one stalling
/// peer can't confuse another peer's tile.
///
/// Stall state machine (same semantics as the pre-multi-publisher single tile):
/// <list type="bullet">
/// <item><b>Empty</b> — no frames yet. Tile shows "Waiting for frames…".</item>
/// <item><b>Active</b> — frames flowing. Render normally.</item>
/// <item><b>Paused</b> — no frames for <see cref="PauseAfter"/>. Freeze on last frame, badge.</item>
/// <item><b>Idle</b> — no frames for <see cref="PauseAfter"/>+<see cref="IdleAfter"/>. Go back to Empty.</item>
/// </list>
/// </summary>
public sealed partial class PublisherTileViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan PauseAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(5);

    private readonly SubscriberSession _session;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly DispatcherTimer _staleTimer;
    private readonly FrameArrivedHandler _frameHandler;
    private readonly TextureArrivedHandler _textureHandler;

    private DateTime _lastFrameUtc = DateTime.MinValue;
    private long _statsPrevFrames;
    private long _statsPrevBytes;
    private DateTime _statsPrevTickUtc = DateTime.MinValue;
    private bool _disposed;

    public PublisherTileViewModel(
        SubscriberSession session,
        string displayName,
        int nominalFrameRate,
        IUiDispatcher uiDispatcher,
        double initialVolume = 1.0,
        bool initialMuted = false)
    {
        _session = session;
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _displayName = displayName;
        _nominalFrameRate = nominalFrameRate > 0 ? nominalFrameRate : 60;
        _volume = Math.Clamp(initialVolume, 0.0, 1.0);
        _isMuted = initialMuted;

        _frameHandler = OnFrameArrived;
        _textureHandler = OnTextureArrived;
        _session.Receiver.FrameArrived += _frameHandler;
        _session.Receiver.TextureArrived += _textureHandler;

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

        // DispatcherTimer needs a WPF Dispatcher instance — WpfUiDispatcher
        // is the single place that owns one, so reach in there for the
        // timer constructor. Every other dispatcher concern in this VM
        // goes through IUiDispatcher, independent of WPF specifics.
        var wpfDispatcher = (uiDispatcher as WpfUiDispatcher)?.Dispatcher
            ?? throw new InvalidOperationException(
                "PublisherTileViewModel requires a WpfUiDispatcher to construct its DispatcherTimer.");
        _staleTimer = new DispatcherTimer(DispatcherPriority.Background, wpfDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _staleTimer.Tick += (_, _) => OnStaleTick();
        _staleTimer.Start();
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
    /// "Connecting…" placeholder instead of a black rectangle.
    /// </summary>
    [ObservableProperty] private bool _hasFirstFrame;

    /// <summary>
    /// True when frames have stopped but we haven't given up yet. Tile freezes on
    /// the last frame and overlays a Paused badge.
    /// </summary>
    [ObservableProperty] private bool _isPaused;

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

    private void OnFrameArrived(in CaptureFrameData frame) => NotifyFrameObserved();

    private void OnTextureArrived(IntPtr nativeTexture, int width, int height, TimeSpan timestamp)
        => NotifyFrameObserved();

    private void NotifyFrameObserved()
    {
        _lastFrameUtc = DateTime.UtcNow;

        // Fast path — already Active, nothing to flip.
        if (HasFirstFrame && !IsPaused)
        {
            return;
        }

        _uiDispatcher.Post(EnterActive);
    }

    private void EnterActive()
    {
        HasFirstFrame = true;
        IsPaused = false;
    }

    private void OnStaleTick()
    {
        if (_lastFrameUtc == DateTime.MinValue)
        {
            return;
        }

        var gap = DateTime.UtcNow - _lastFrameUtc;
        if (HasFirstFrame && !IsPaused && gap >= PauseAfter)
        {
            IsPaused = true;
            return;
        }
        if (IsPaused && gap >= PauseAfter + IdleAfter)
        {
            // Give up. Tile goes back to "Connecting…" until frames resume.
            IsPaused = false;
            HasFirstFrame = false;
        }
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
        Stats = string.IsNullOrEmpty(RenderStatsLine) ? body : body + "\n" + RenderStatsLine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _staleTimer.Stop();
        try { _session.Receiver.FrameArrived -= _frameHandler; } catch { }
        try { _session.Receiver.TextureArrived -= _textureHandler; } catch { }

        try { await _session.DisposeAsync().ConfigureAwait(false); } catch { }
    }
}
