namespace VicoScreenShare.Desktop.App;

using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VicoScreenShare.Client;
using VicoScreenShare.Client.Services;
using VicoScreenShare.Desktop.App.Services;
using VicoScreenShare.Desktop.App.ViewModels;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // Escape inside the popup dismisses the overlay. Popups don't forward
    // keyboard events to the owning window, so the handler lives here on
    // the popup's content.
    private void OnOverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is INavigationHost nav) nav.CloseOverlay();
        e.Handled = true;
    }

    // Click on the backdrop (outside the settings card) dismisses the overlay.
    private void OnOverlayBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is INavigationHost nav) nav.CloseOverlay();
    }

    // Card swallows the click so it doesn't bubble to the backdrop and
    // dismiss the overlay when the user interacts with form controls.
    private void OnOverlayCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
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
