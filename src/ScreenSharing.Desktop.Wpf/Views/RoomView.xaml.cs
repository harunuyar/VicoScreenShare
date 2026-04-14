using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenSharing.Desktop.App.Rendering;
using ScreenSharing.Desktop.App.ViewModels;

namespace ScreenSharing.Desktop.App.Views;

public partial class RoomView : UserControl
{
    private DispatcherTimer? _paintStatsTimer;

    // Per-renderer previous counters so the input/painted deltas
    // produce a clean fps reading instead of leaking across tiles.
    private long _remotePrevPainted, _remotePrevInput;
    private long _selfPrevPainted, _selfPrevInput;
    private DateTime _prevTickUtc = DateTime.MinValue;

    public RoomView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _paintStatsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _paintStatsTimer.Tick += OnPaintStatsTick;
        _paintStatsTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _paintStatsTimer?.Stop();
        _paintStatsTimer = null;
    }

    /// <summary>
    /// Polls each renderer (remote stream + self preview) twice a
    /// second and pushes a "paint: X fps" line into the matching slot
    /// on the VM. The stats timer in <see cref="RoomViewModel"/>
    /// appends those lines to the per-tile stats overlays.
    /// </summary>
    private void OnPaintStatsTick(object? sender, EventArgs e)
    {
        if (DataContext is not RoomViewModel vm) return;

        var now = DateTime.UtcNow;
        if (_prevTickUtc == DateTime.MinValue)
        {
            _prevTickUtc = now;
            return;
        }
        var elapsed = Math.Max(0.001, (now - _prevTickUtc).TotalSeconds);
        _prevTickUtc = now;

        vm.RemoteRenderStatsLine = SnapshotRenderer(
            FindName("RemoteRenderer") as D3DImageVideoRenderer,
            ref _remotePrevInput, ref _remotePrevPainted, elapsed);
        vm.SelfRenderStatsLine = SnapshotRenderer(
            FindName("SelfRenderer") as D3DImageVideoRenderer,
            ref _selfPrevInput, ref _selfPrevPainted, elapsed);
    }

    private static string? SnapshotRenderer(
        D3DImageVideoRenderer? renderer,
        ref long prevInput, ref long prevPainted,
        double elapsedSeconds)
    {
        if (renderer is null) return null;

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
        if (DataContext is not RoomViewModel vm) return;
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
