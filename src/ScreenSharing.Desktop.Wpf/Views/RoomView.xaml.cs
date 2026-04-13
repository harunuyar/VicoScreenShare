using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenSharing.Desktop.Wpf.Rendering;
using ScreenSharing.Desktop.Wpf.ViewModels;

namespace ScreenSharing.Desktop.Wpf.Views;

public partial class RoomView : UserControl
{
    private DispatcherTimer? _paintStatsTimer;
    private long _prevPainted;
    private long _prevInput;
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
    /// Pulls the D3DImageVideoRenderer's input/paint counters twice a
    /// second and pokes a "paint: X fps / dropped: Y" line onto the
    /// RoomViewModel so it shows up in the stats overlay. Matches the
    /// Avalonia client's stats format exactly.
    /// </summary>
    private void OnPaintStatsTick(object? sender, EventArgs e)
    {
        if (DataContext is not RoomViewModel vm) return;
        var renderer = FindVideoRenderer();
        if (renderer is null) return;

        // Single atomic read of input+painted under the renderer's
        // lock. Reading the two counters separately outside the lock
        // races against an in-flight OnFrameArrivedCore (which bumps
        // input at the top and painted at the bottom), so a random
        // snapshot can land mid-frame and show painted = input - 1,
        // which the user sees as a constant phantom "dropped: 1" line.
        var (input, painted, lastPaintMs) = renderer.Snapshot();

        var now = DateTime.UtcNow;
        if (_prevTickUtc == DateTime.MinValue)
        {
            _prevTickUtc = now;
            _prevPainted = painted;
            _prevInput = input;
            return;
        }

        var elapsed = Math.Max(0.001, (now - _prevTickUtc).TotalSeconds);
        _prevTickUtc = now;

        var deltaPainted = painted - _prevPainted;
        var deltaInput = input - _prevInput;
        _prevPainted = painted;
        _prevInput = input;

        var paintFps = deltaPainted / elapsed;
        var inputFps = deltaInput / elapsed;
        var droppedFps = Math.Max(0, inputFps - paintFps);

        vm.RenderStatsLine =
            $"paint:  {paintFps:F1} fps ({lastPaintMs:F1} ms/frame)\n" +
            $"dropped:{droppedFps:F1} fps (renderer behind)";
    }

    private D3DImageVideoRenderer? FindVideoRenderer()
    {
        // The renderer is inside a Border inside a Grid inside the
        // root Grid; walking the named tree is brittle. Instead use
        // FindName which resolves by x:Name — or simpler, iterate the
        // logical tree looking for the first D3DImageVideoRenderer.
        return FindDescendant<D3DImageVideoRenderer>(this);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
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
