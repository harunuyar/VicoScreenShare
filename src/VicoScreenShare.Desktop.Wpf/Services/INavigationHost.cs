namespace VicoScreenShare.Desktop.App.Services;

/// <summary>
/// Abstracts how view models ask the shell to switch screens. Same
/// contract as the one the WinUI attempt had: you hand it a fully-built
/// successor VM, the shell figures out which view to put it in.
/// </summary>
public interface INavigationHost
{
    void NavigateTo(object viewModel);
    bool CanGoBack { get; }

    /// <summary>
    /// Mount a view model as a modal overlay on top of the current view. The
    /// shell renders it centered with a dimmed background — the underlying
    /// page stays mounted but is not interactive. Pass null or call
    /// <see cref="CloseOverlay"/> to dismiss.
    /// </summary>
    void ShowOverlay(object overlayViewModel);

    void CloseOverlay();

    /// <summary>
    /// Process-wide UI dispatcher, captured on the UI thread at composition
    /// time. View models receive this instead of calling
    /// <c>Dispatcher.CurrentDispatcher</c> themselves, so a VM constructed
    /// on a thread-pool thread can't accidentally bind to a dead dispatcher.
    /// </summary>
    IUiDispatcher UiDispatcher { get; }
}
