namespace VicoScreenShare.Desktop.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VicoScreenShare.Client;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Platform;

/// <summary>
/// Custom share picker — lists windows and monitors with live
/// thumbnails and lets the user commit to one. Completes
/// <see cref="ResultTask"/> with a <see cref="PickedCaptureRequest"/>
/// when the user clicks Share, or null when they cancel / close /
/// open Settings instead.
/// <para>
/// Quality tuning is NOT done here — the picker exposes a gear button
/// that dismisses itself and opens the full Settings dialog. That way
/// one set of controls governs every share (no drift between picker
/// overrides and saved preferences) and a single settings surface is
/// kept consistent across the app.
/// </para>
/// </summary>
public sealed partial class SharePickerViewModel : ViewModelBase
{
    // Per-tile preview frame rate. Capped capture resolution (see
    // PreviewMaxDimension below) means each tile's frame is tiny, so
    // 15 fps across 7+ tiles is nothing for the GPU — gives the
    // picker a genuinely live feel without hammering the system.
    private const int PreviewFrameRate = 15;

    // Cap on each captured frame's longer side. 320px covers our
    // 264×160 tiles with some oversample headroom; the OS compositor
    // handles the downscale during capture so the frame arrives at
    // the right size with its proper filter applied — avoiding the
    // brute-force 10× bilinear scale D3DImageVideoRenderer otherwise
    // has to do for a 2560×1440 source into a 264px tile.
    private const int PreviewMaxDimension = 320;

    private readonly ICaptureTargetEnumerator _enumerator;
    private readonly ICaptureProvider? _captureProvider;
    private readonly VideoSettings _settings;
    private readonly TaskCompletionSource<PickedCaptureRequest?> _resultTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action _closeOverlay;
    private readonly Action? _openSettings;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ICaptureSource> _liveSources = new();

    public SharePickerViewModel(
        ICaptureTargetEnumerator enumerator,
        ICaptureProvider? captureProvider,
        VideoSettings settings,
        Action closeOverlay,
        Action? openSettings = null)
    {
        _enumerator = enumerator;
        _captureProvider = captureProvider;
        _settings = settings;
        _closeOverlay = closeOverlay;
        _openSettings = openSettings;
        _ = LoadTargetsAsync();
    }

    /// <summary>
    /// Awaitable that resolves when the user has chosen a target (Share)
    /// or dismissed the picker (Cancel / overlay-backdrop click).
    /// </summary>
    public Task<PickedCaptureRequest?> ResultTask => _resultTcs.Task;

    public ObservableCollection<CaptureTargetTileViewModel> WindowTiles { get; } = new();

    public ObservableCollection<CaptureTargetTileViewModel> MonitorTiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowsTabActive), nameof(IsScreensTabActive))]
    private SharePickerTab _activeTab = SharePickerTab.Windows;

    public bool IsWindowsTabActive => ActiveTab == SharePickerTab.Windows;
    public bool IsScreensTabActive => ActiveTab == SharePickerTab.Screens;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShareCommand))]
    private CaptureTargetTileViewModel? _selectedTile;

    [ObservableProperty]
    private string? _statusMessage;

    [RelayCommand]
    private void SelectTab(string tabName)
    {
        ActiveTab = string.Equals(tabName, "Screens", StringComparison.OrdinalIgnoreCase)
            ? SharePickerTab.Screens
            : SharePickerTab.Windows;
    }

    [RelayCommand]
    private void SelectTile(CaptureTargetTileViewModel tile)
    {
        if (tile is null)
        {
            return;
        }
        // Single-select across both tabs — picking a window while a
        // monitor was selected (or vice versa) unselects the old one.
        foreach (var t in WindowTiles.Concat(MonitorTiles))
        {
            t.IsSelected = ReferenceEquals(t, tile);
        }
        SelectedTile = tile;
    }

    [RelayCommand(CanExecute = nameof(CanShare))]
    private void Share()
    {
        if (SelectedTile is null)
        {
            return;
        }

        // Settings dialog writes straight to the same VideoSettings
        // instance, so _settings is always the current saved value —
        // no separate clone / merge step needed here.
        var req = new PickedCaptureRequest(SelectedTile.Target, _settings);
        _resultTcs.TrySetResult(req);
        _closeOverlay();
    }

    private bool CanShare() => SelectedTile is not null;

    [RelayCommand]
    private void Cancel()
    {
        _resultTcs.TrySetResult(null);
        _closeOverlay();
    }

    /// <summary>
    /// Dismiss the picker and open the Settings dialog. Picker resolves
    /// as cancelled — the user explicitly chose to tune settings rather
    /// than share right now. When they're done, they can reopen the
    /// picker with updated saved settings in effect.
    /// </summary>
    [RelayCommand]
    private void OpenSettings()
    {
        _resultTcs.TrySetResult(null);
        _closeOverlay();
        _openSettings?.Invoke();
    }

    /// <summary>
    /// Called by the dialog host when the overlay is being torn down
    /// (backdrop click, Esc). Resolves the result task as "cancelled"
    /// if no Share was clicked so the awaiting caller is never stuck.
    /// </summary>
    public void NotifyOverlayClosed()
    {
        _resultTcs.TrySetResult(null);
        _cts.Cancel();
        DisposeLiveSources();
    }

    /// <summary>
    /// Called by the dialog host after the picker closes (user clicked
    /// Share, Cancel, or backdrop) to shut down all the per-tile WGC
    /// capture sessions we started. Must run after the result is read
    /// so the caller's SubscriberSession / CaptureStreamer can keep
    /// its own source if the user picked one we were previewing.
    /// </summary>
    public void DisposeLiveSources()
    {
        foreach (var tile in WindowTiles.Concat(MonitorTiles))
        {
            tile.ClearCaptureSource();
        }
        foreach (var source in _liveSources)
        {
            _ = DisposeSourceAsync(source);
        }
        _liveSources.Clear();
    }

    private static async Task DisposeSourceAsync(ICaptureSource source)
    {
        try { await source.StopAsync().ConfigureAwait(false); } catch { }
        try { await source.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    private async Task LoadTargetsAsync()
    {
        try
        {
            var targets = await Task.Run(() => _enumerator.EnumerateAsync(_cts.Token)).ConfigureAwait(true);
            DebugLog.Write($"[picker] enumerated {targets.Count} targets");

            foreach (var t in targets)
            {
                var vm = new CaptureTargetTileViewModel(t);
                (t.Kind == CaptureTargetKind.Window ? WindowTiles : MonitorTiles).Add(vm);
            }

            IsLoading = false;
            if (WindowTiles.Count == 0 && MonitorTiles.Count > 0)
            {
                ActiveTab = SharePickerTab.Screens;
            }

            if (_captureProvider is null)
            {
                DebugLog.Write("[picker] no capture provider registered — tiles won't have live previews");
                return;
            }

            // Start a low-fps live preview per tile using the same
            // WindowsCaptureSource + D3DImageVideoRenderer pipeline
            // the room's self-preview uses.
            foreach (var tile in WindowTiles.Concat(MonitorTiles))
            {
                _ = StartLivePreviewAsync(tile);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[picker] LoadTargetsAsync threw: {ex.GetType().Name}: {ex.Message}");
            StatusMessage = $"Couldn't enumerate targets: {ex.Message}";
            IsLoading = false;
        }
    }

    private async Task StartLivePreviewAsync(CaptureTargetTileViewModel tile)
    {
        if (_captureProvider is null || _cts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var source = await _captureProvider.CreateSourceForTargetAsync(tile.Target, PreviewFrameRate, PreviewMaxDimension).ConfigureAwait(true);
            if (source is null || _cts.IsCancellationRequested)
            {
                if (source is not null)
                {
                    _ = DisposeSourceAsync(source);
                }
                DebugLog.Write($"[picker] live preview: CreateSourceForTargetAsync returned null for \"{tile.DisplayName}\"");
                return;
            }

            _liveSources.Add(source);
            tile.AttachCaptureSource(source);
            await source.StartAsync().ConfigureAwait(true);
            DebugLog.Write($"[picker] live preview started for \"{tile.DisplayName}\" @ {PreviewFrameRate} fps");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[picker] StartLivePreviewAsync threw for \"{tile.DisplayName}\": {ex.GetType().Name}: {ex.Message}");
        }
    }

}

public enum SharePickerTab
{
    Windows = 0,
    Screens = 1,
}
