using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Services;

namespace ScreenSharing.Desktop.App.ViewModels;

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
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _staleTimer;
    private readonly FrameArrivedHandler _frameHandler;
    private readonly TextureArrivedHandler _textureHandler;

    private DateTime _lastFrameUtc = DateTime.MinValue;
    private long _statsPrevFrames;
    private long _statsPrevBytes;
    private DateTime _statsPrevTickUtc = DateTime.MinValue;
    private bool _disposed;

    public PublisherTileViewModel(SubscriberSession session, string displayName, int nominalFrameRate)
    {
        _session = session;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _displayName = displayName;
        _nominalFrameRate = nominalFrameRate > 0 ? nominalFrameRate : 60;

        _frameHandler = OnFrameArrived;
        _textureHandler = OnTextureArrived;
        _session.Receiver.FrameArrived += _frameHandler;
        _session.Receiver.TextureArrived += _textureHandler;

        _staleTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
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
        if (HasFirstFrame && !IsPaused) return;

        if (_dispatcher.CheckAccess()) EnterActive();
        else _dispatcher.BeginInvoke(new Action(EnterActive));
    }

    private void EnterActive()
    {
        HasFirstFrame = true;
        IsPaused = false;
    }

    private void OnStaleTick()
    {
        if (_lastFrameUtc == DateTime.MinValue) return;

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
            $"frames: {r.FramesDecoded}";

        Stats = string.IsNullOrEmpty(RenderStatsLine) ? core : core + "\n" + RenderStatsLine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _staleTimer.Stop();
        try { _session.Receiver.FrameArrived -= _frameHandler; } catch { }
        try { _session.Receiver.TextureArrived -= _textureHandler; } catch { }

        try { await _session.DisposeAsync().ConfigureAwait(false); } catch { }
    }
}
