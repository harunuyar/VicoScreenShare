using System;
using System.Windows;
using System.Windows.Interop;
using ScreenSharing.Client;
using ScreenSharing.Client.Services;
using ScreenSharing.Desktop.Wpf.Services;
using ScreenSharing.Desktop.Wpf.ViewModels;

namespace ScreenSharing.Desktop.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NavigationService nav) return;

        var identity = new IdentityStore();
        var settingsStore = new SettingsStore();
        var settings = settingsStore.LoadOrCreate();

        var hwndProvider = new Func<IntPtr>(() => new WindowInteropHelper(this).Handle);
        var captureProvider = ClientHost.CaptureProviderFactory?.Invoke(hwndProvider);

        var home = new HomeViewModel(
            identity,
            () => new SignalingClient(),
            nav,
            settings,
            settingsStore,
            captureProvider);

        nav.NavigateTo(home);
    }
}
