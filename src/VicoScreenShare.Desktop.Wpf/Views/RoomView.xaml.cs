namespace VicoScreenShare.Desktop.App.Views;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VicoScreenShare.Desktop.App.Rendering;
using VicoScreenShare.Desktop.App.ViewModels;

public partial class RoomView : UserControl
{
    // Idle threshold after which the tile chrome fades out. Resets on
    // any mouse move anywhere in the room view; any re-entry or twitch
    // brings the chrome back.
    private static readonly TimeSpan ChromeIdleTimeout = TimeSpan.FromSeconds(3);

    private DispatcherTimer? _paintStatsTimer;
    private DispatcherTimer? _chromeIdleTimer;

    // Previous counters for self-preview renderer only. Per-tile paint fps is
    // Phase 6 polish — the ItemsControl-mounted D3D renderers would need a
    // per-item attached property or behavior to feed their counters back.
    private long _selfPrevPainted, _selfPrevInput;
    private DateTime _prevTickUtc = DateTime.MinValue;


    public RoomView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // PreviewKeyDown at the UserControl level so fullscreen/Focus shortcuts
        // fire before any child control can eat the key. Settings overlay lives
        // in its own popup HWND, so its Escape handler doesn't compete.
        PreviewKeyDown += OnPreviewKeyDown;
        // PreviewMouseMove so tile-level Panels don't swallow the event
        // before this handler sees it — we want every mouse twitch
        // anywhere in the room to count as "user is here, keep chrome
        // visible."
        PreviewMouseMove += OnAnyMouseMove;
        MouseLeave += OnAnyMouseMove;
        MouseEnter += OnAnyMouseMove;
        Focusable = true;
    }

    private void OnAnyMouseMove(object sender, MouseEventArgs e) => NoteUserActivity();

    private void NoteUserActivity()
    {
        if (DataContext is RoomViewModel vm)
        {
            vm.IsChromeVisible = true;
        }
        // Restart the countdown from zero — any motion resets the
        // idle window, so a user constantly hovering never hides
        // chrome.
        _chromeIdleTimer?.Stop();
        _chromeIdleTimer?.Start();
    }

    private void OnChromeIdleTick(object? sender, EventArgs e)
    {
        _chromeIdleTimer?.Stop();
        if (DataContext is RoomViewModel vm)
        {
            vm.IsChromeVisible = false;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not RoomViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (vm.ExitOverlayModeCommand.CanExecute(null))
                {
                    vm.ExitOverlayModeCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.F11:
                // F11 toggles fullscreen on the focused tile (or first tile).
                var target = vm.FocusedPublisherPeerId ?? vm.Tiles.FirstOrDefault()?.PublisherPeerId;
                if (target is Guid id && vm.ToggleFullscreenCommand.CanExecute(id))
                {
                    vm.ToggleFullscreenCommand.Execute(id);
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _paintStatsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _paintStatsTimer.Tick += OnPaintStatsTick;
        _paintStatsTimer.Start();

        _chromeIdleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = ChromeIdleTimeout,
        };
        _chromeIdleTimer.Tick += OnChromeIdleTick;
        _chromeIdleTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _paintStatsTimer?.Stop();
        _paintStatsTimer = null;
        _chromeIdleTimer?.Stop();
        _chromeIdleTimer = null;
    }

    /// <summary>
    /// Polls each renderer (remote stream + self preview) twice a
    /// second and pushes a "paint: X fps" line into the matching slot
    /// on the VM. The stats timer in <see cref="RoomViewModel"/>
    /// appends those lines to the per-tile stats overlays.
    /// </summary>
    private void OnPaintStatsTick(object? sender, EventArgs e)
    {
        if (DataContext is not RoomViewModel vm)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_prevTickUtc == DateTime.MinValue)
        {
            _prevTickUtc = now;
            return;
        }
        var elapsed = Math.Max(0.001, (now - _prevTickUtc).TotalSeconds);
        _prevTickUtc = now;

        vm.SelfRenderStatsLine = SnapshotRenderer(
            FindName("SelfRenderer") as D3DImageVideoRenderer,
            ref _selfPrevInput, ref _selfPrevPainted, elapsed);
    }

    private static string? SnapshotRenderer(
        D3DImageVideoRenderer? renderer,
        ref long prevInput, ref long prevPainted,
        double elapsedSeconds)
    {
        if (renderer is null)
        {
            return null;
        }

        // Single atomic read of input+painted under the renderer's
        // lock. Reading the two counters separately outside the lock
        // races against an in-flight OnFrameArrivedCore (which bumps
        // input at the top and painted at the bottom), so a random
        // snapshot can land mid-frame and show painted = input - 1,
        // which the user sees as a constant phantom "dropped: 1" line.
        var (input, painted, lastPaintMs) = renderer.Snapshot();

        var deltaPainted = painted - prevPainted;
        var deltaInput = input - prevInput;
        prevPainted = painted;
        prevInput = input;

        var paintFps = deltaPainted / elapsedSeconds;
        var inputFps = deltaInput / elapsedSeconds;
        var droppedFps = Math.Max(0, inputFps - paintFps);

        return
            $"paint:  {paintFps:F1} fps ({lastPaintMs:F1} ms/frame)\n" +
            $"dropped:{droppedFps:F1} fps (renderer behind)";
    }

    private void OnCopyRoomIdClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RoomViewModel vm)
        {
            return;
        }

        try
        {
            Clipboard.SetText(vm.RoomId);
            vm.OnRoomIdCopied();
        }
        catch (Exception)
        {
            // Clipboard access races with other apps sometimes. Not fatal.
        }
    }

}
