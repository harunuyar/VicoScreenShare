namespace ScreenSharing.Desktop.Wpf.Services;

/// <summary>
/// Abstracts how view models ask the shell to switch screens. Same
/// contract as the one the WinUI attempt had: you hand it a fully-built
/// successor VM, the shell figures out which view to put it in.
/// </summary>
public interface INavigationHost
{
    void NavigateTo(object viewModel);
    bool CanGoBack { get; }
}
