namespace VicoScreenShare.Desktop.App.Views;

using System;
using System.Threading.Tasks;
using System.Windows;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Desktop.App.Services;
using VicoScreenShare.Desktop.App.ViewModels;

/// <summary>
/// Opens the custom share picker as an in-window overlay (same
/// mechanism Settings uses). Returns a
/// <see cref="PickedCaptureRequest"/> if the user clicked Share, or
/// null if they cancelled. The underlying page stays mounted while
/// the picker is open so the room view keeps its state.
/// </summary>
public static class SharePickerDialog
{
    public static async Task<PickedCaptureRequest?> ShowAsync(
        ICaptureTargetEnumerator enumerator,
        ICaptureProvider? captureProvider,
        VideoSettings settings,
        Action? openSettings = null)
    {
        if (Application.Current?.MainWindow?.DataContext is not INavigationHost nav
            || nav is not NavigationService svc)
        {
            return null;
        }

        // Wide overlay, centered — tile grid is 2 columns of 264-wide
        // tiles plus margins, ~720 fits comfortably without clipping.
        svc.OverlayWidth = 720;
        svc.OverlayMaxHeight = 760;
        svc.OverlayHorizontalAlignment = HorizontalAlignment.Center;
        svc.OverlayVerticalAlignment = VerticalAlignment.Center;
        svc.OverlayMargin = new Thickness(0);

        var vm = new SharePickerViewModel(enumerator, captureProvider, settings, nav.CloseOverlay, openSettings);
        nav.ShowOverlay(vm);

        // Backdrop-click / Esc paths close the overlay directly without
        // going through the VM; watch for that so the awaiting result
        // task resolves as "cancelled" and we free the picker resources.
        void OnOverlayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NavigationService.ActiveOverlay) && svc.ActiveOverlay is null)
            {
                vm.NotifyOverlayClosed();
            }
        }
        svc.PropertyChanged += OnOverlayChanged;
        try
        {
            return await vm.ResultTask.ConfigureAwait(true);
        }
        finally
        {
            svc.PropertyChanged -= OnOverlayChanged;
            // Dispose the per-tile WGC sources regardless of which path
            // closed the picker; the share target's own capture session
            // is a fresh instance built by the caller.
            vm.DisposeLiveSources();
        }
    }
}
