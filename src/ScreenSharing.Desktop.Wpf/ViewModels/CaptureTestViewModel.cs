using System;
using CommunityToolkit.Mvvm.Input;
using ScreenSharing.Desktop.App.Services;

namespace ScreenSharing.Desktop.App.ViewModels;

/// <summary>
/// Tiny navigation-routing view model for the capture-test page. The
/// page itself is mostly imperative D3D + WGC code that lives in the
/// view's code-behind, so this VM exists purely to satisfy the
/// MVVM/navigation pattern the rest of the shell uses (a VM that
/// <see cref="NavigationService"/> can route to, plus a back command
/// that produces the previous VM).
/// </summary>
public sealed partial class CaptureTestViewModel : ViewModelBase
{
    private readonly INavigationHost _navigation;
    private readonly Func<ViewModelBase> _backFactory;

    public CaptureTestViewModel(INavigationHost navigation, Func<ViewModelBase> backFactory)
    {
        _navigation = navigation;
        _backFactory = backFactory;
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigation.NavigateTo(_backFactory());
    }
}
