using CommunityToolkit.Mvvm.ComponentModel;
using ScreenSharing.Client.ViewModels;

namespace ScreenSharing.Client.Services;

/// <summary>
/// Minimal navigation service for Phase 1: exposes a single
/// <see cref="CurrentViewModel"/> that the root view binds to via a
/// <c>ContentControl</c> + <c>DataTemplates</c> lookup.
/// </summary>
public sealed partial class NavigationService : ObservableObject
{
    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    public void NavigateTo(ViewModelBase viewModel) => CurrentViewModel = viewModel;
}
