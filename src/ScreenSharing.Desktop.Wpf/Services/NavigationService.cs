using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ScreenSharing.Desktop.Wpf.Services;

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

    public bool CanGoBack => false;

    public void NavigateTo(object viewModel)
    {
        if (viewModel is null) throw new ArgumentNullException(nameof(viewModel));
        Current = viewModel;
    }
}
