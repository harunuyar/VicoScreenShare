using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Services;
using ScreenSharing.Client.ViewModels;

namespace ScreenSharing.Client;

public partial class App : Application
{
    /// <summary>
    /// Set by the platform-specific desktop host (e.g. ScreenSharing.Desktop.Windows)
    /// before Avalonia starts. The <see cref="Func{T, TResult}"/> receives a callback
    /// that returns the current main window HWND on demand and returns an
    /// <see cref="ICaptureProvider"/> wired to that window.
    /// </summary>
    public static Func<Func<IntPtr>, ICaptureProvider>? CaptureProviderFactory { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var identity = new IdentityStore();
            var navigation = new NavigationService();
            var settings = new ClientSettings();

            var mainWindow = new MainWindow
            {
                DataContext = navigation,
            };

            ICaptureProvider? captureProvider = null;
            if (CaptureProviderFactory is not null)
            {
                Func<IntPtr> hwndProvider = () =>
                    mainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                captureProvider = CaptureProviderFactory(hwndProvider);
            }

            var home = new HomeViewModel(
                identity,
                () => new SignalingClient(),
                navigation,
                settings,
                captureProvider);
            navigation.NavigateTo(home);

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
