namespace VicoScreenShare.Desktop.App.Services;

using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

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
    public NavigationService()
    {
        // NavigationService is instantiated from MainWindow.xaml as the
        // window's DataContext, which WPF constructs on the UI thread.
        // Capture the UI dispatcher here, exactly once, and flow it to every
        // view model via the INavigationHost interface. View models never
        // call Dispatcher.CurrentDispatcher themselves.
        UiDispatcher = new WpfUiDispatcher(Dispatcher.CurrentDispatcher);
    }

    public IUiDispatcher UiDispatcher { get; }

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

    /// <summary>
    /// Preferred width of the current overlay, in logical pixels. The
    /// Border hosting <see cref="ActiveOverlay"/> binds its Width to
    /// this so different overlays (compact Settings panel vs wide share
    /// picker) can claim the size they need. Set immediately before
    /// <see cref="ShowOverlay"/>; reset to the default (460) on close.
    /// </summary>
    [ObservableProperty]
    private double _overlayWidth = 460;

    /// <summary>
    /// Max height of the current overlay. Same semantics as
    /// <see cref="OverlayWidth"/>.
    /// </summary>
    [ObservableProperty]
    private double _overlayMaxHeight = 640;

    /// <summary>
    /// Horizontal alignment of the overlay card within the window.
    /// Settings anchors to the right (near its button); the share
    /// picker centers. Defaults to Right to match the pre-picker
    /// behavior Settings dialog already relied on.
    /// </summary>
    [ObservableProperty]
    private System.Windows.HorizontalAlignment _overlayHorizontalAlignment = System.Windows.HorizontalAlignment.Right;

    /// <summary>
    /// Vertical alignment of the overlay card within the window.
    /// Settings anchors to the top, picker centers.
    /// </summary>
    [ObservableProperty]
    private System.Windows.VerticalAlignment _overlayVerticalAlignment = System.Windows.VerticalAlignment.Top;

    /// <summary>Outer margin of the overlay card. Lets Settings sit
    /// 64px below the title bar / 16px from the right edge while the
    /// picker centers at margin zero.</summary>
    [ObservableProperty]
    private System.Windows.Thickness _overlayMargin = new(0, 64, 16, 16);

    public bool CanGoBack => false;

    public void NavigateTo(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Current = viewModel;
    }

    public void ShowOverlay(object overlayViewModel)
    {
        ArgumentNullException.ThrowIfNull(overlayViewModel);
        ActiveOverlay = overlayViewModel;
    }

    public void CloseOverlay()
    {
        ActiveOverlay = null;
        OverlayWidth = 460;
        OverlayMaxHeight = 640;
        OverlayHorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        OverlayVerticalAlignment = System.Windows.VerticalAlignment.Top;
        OverlayMargin = new System.Windows.Thickness(0, 64, 16, 16);
    }
}
