using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
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

    /// <summary>
    /// Set by the platform-specific desktop host before Avalonia starts. The
    /// Windows host registers VP8 (always) and H.264 (when FFmpeg is found);
    /// if unset, <see cref="RoomViewModel"/> falls back to a fresh VP8-only
    /// catalog so the app still works in test and headless scenarios.
    /// </summary>
    public static VideoCodecCatalog? VideoCodecCatalog { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Start each run with a fresh debug log so users can find only the
            // current session's diagnostic data at DebugLog.FilePath.
            DebugLog.Reset();
            DebugLog.Write($"== ScreenSharing client start @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==");

            var identity = new IdentityStore();
            var navigation = new NavigationService();
            var settingsStore = new SettingsStore();
            var settings = settingsStore.LoadOrCreate();

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
                settingsStore,
                captureProvider);
            navigation.NavigateTo(home);

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
