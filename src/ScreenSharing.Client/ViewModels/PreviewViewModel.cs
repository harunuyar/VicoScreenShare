using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Rendering;
using ScreenSharing.Client.Services;

namespace ScreenSharing.Client.ViewModels;

/// <summary>
/// Phase 2 preview screen: lets the user open the system capture picker, select a
/// window or monitor, and watch a live preview rendered via
/// <see cref="WriteableBitmapRenderer"/>. No network, no encoder — just proving
/// the capture pipeline end-to-end on the local machine.
/// </summary>
public sealed partial class PreviewViewModel : ViewModelBase, IDisposable
{
    private readonly ICaptureProvider _captureProvider;
    private readonly NavigationService _navigation;
    private readonly Func<HomeViewModel> _homeFactory;
    private readonly WriteableBitmapRenderer _renderer = new();

    private ICaptureSource? _source;
    private readonly Stopwatch _fpsClock = new();
    private int _framesSinceLastTick;
    private DispatcherTimer? _fpsTimer;

    public PreviewViewModel(
        ICaptureProvider captureProvider,
        NavigationService navigation,
        Func<HomeViewModel> homeFactory)
    {
        _captureProvider = captureProvider;
        _navigation = navigation;
        _homeFactory = homeFactory;
        _renderer.FrameRendered += OnFrameRendered;
    }

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private string? _statusMessage = "Click 'Share screen' to pick a window or monitor.";

    [ObservableProperty]
    private string? _sourceLabel;

    [ObservableProperty]
    private string _fps = "-- fps";

    [ObservableProperty]
    private bool _isCapturing;

    [RelayCommand]
    private async Task ShareScreenAsync()
    {
        if (IsCapturing) return;

        StatusMessage = "Opening picker...";
        try
        {
            var source = await _captureProvider.PickSourceAsync().ConfigureAwait(true);
            if (source is null)
            {
                StatusMessage = "Picker cancelled.";
                return;
            }

            _source = source;
            SourceLabel = source.DisplayName;
            _renderer.Attach(source);
            source.Closed += OnSourceClosed;

            await source.StartAsync().ConfigureAwait(true);

            IsCapturing = true;
            StatusMessage = null;
            StartFpsTimer();
        }
        catch (PlatformNotSupportedException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Capture failed: {ex.Message}";
            await StopCaptureInternalAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task StopCaptureAsync() => StopCaptureInternalAsync();

    [RelayCommand]
    private async Task GoBackAsync()
    {
        await StopCaptureInternalAsync().ConfigureAwait(true);
        _navigation.NavigateTo(_homeFactory());
    }

    private async Task StopCaptureInternalAsync()
    {
        StopFpsTimer();

        if (_source is not null)
        {
            _source.Closed -= OnSourceClosed;
            _renderer.Detach(_source);
            try { await _source.StopAsync().ConfigureAwait(true); } catch { }
            try { await _source.DisposeAsync().ConfigureAwait(true); } catch { }
            _source = null;
        }

        IsCapturing = false;
        PreviewImage = null;
        Fps = "-- fps";
    }

    private void OnSourceClosed()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            StatusMessage = "Capture source closed.";
            await StopCaptureInternalAsync().ConfigureAwait(true);
        });
    }

    private void OnFrameRendered()
    {
        Interlocked.Increment(ref _framesSinceLastTick);
        // The renderer created/refreshed its bitmap. Push the reference to the view
        // if we haven't already.
        if (!ReferenceEquals(PreviewImage, _renderer.CurrentBitmap))
        {
            Dispatcher.UIThread.Post(() => PreviewImage = _renderer.CurrentBitmap);
        }
    }

    private void StartFpsTimer()
    {
        _framesSinceLastTick = 0;
        _fpsClock.Restart();
        _fpsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnFpsTick);
        _fpsTimer.Start();
    }

    private void StopFpsTimer()
    {
        _fpsTimer?.Stop();
        _fpsTimer = null;
    }

    private void OnFpsTick(object? sender, EventArgs e)
    {
        var frames = Interlocked.Exchange(ref _framesSinceLastTick, 0);
        Fps = $"{frames} fps";
    }

    public void Dispose()
    {
        _ = StopCaptureInternalAsync();
        _renderer.FrameRendered -= OnFrameRendered;
        _renderer.Dispose();
    }
}
