using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VicoScreenShare.Desktop.App.Services;

/// <summary>
/// Tiny WPF navigation shim: a single <see cref="Current"/> property
/// that MainWindow binds to, plus a <see cref="NavigateTo"/> method view
/// models call when they produce a successor. The WPF shell uses a
/// DataTemplate map in MainWindow.xaml to render the right View for
/// whichever ViewModel is currently set.
///
/// Simpler than Frame/Page navigation — the app only has three screens
/// (Home, Room, Settings) and no back-stack to speak of.
/// </summary>
public sealed partial class NavigationService : ObservableObject, INavigationHost
{
    [ObservableProperty]
    private object? _current;

    /// <summary>
    /// The active in-window overlay (e.g. Settings dialog), or null when no
    /// overlay is open. MainWindow binds an overlay layer to this: visible
    /// when non-null, hidden otherwise. Underlying <see cref="Current"/> page
    /// stays mounted so capture sessions, room media pipelines, etc. keep
    /// running while the overlay is up.
    /// </summary>
    [ObservableProperty]
    private object? _activeOverlay;

    public bool CanGoBack => false;

    public void NavigateTo(object viewModel)
    {
        if (viewModel is null) throw new ArgumentNullException(nameof(viewModel));
        Current = viewModel;
    }

    public void ShowOverlay(object overlayViewModel)
    {
        if (overlayViewModel is null) throw new ArgumentNullException(nameof(overlayViewModel));
        ActiveOverlay = overlayViewModel;
    }

    public void CloseOverlay() => ActiveOverlay = null;
}
