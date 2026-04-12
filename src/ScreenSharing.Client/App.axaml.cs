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

    /// <summary>
    /// Hook for the desktop host to register additional codec factories AFTER
    /// the debug log has been reset. Without this indirection, any log line
    /// written during FFmpeg native-library probing in <c>Program.Main</c>
    /// gets wiped by <see cref="DebugLog.Reset"/> below, and we lose the
    /// exact error that would tell us why a codec failed to load.
    /// </summary>
    public static Action<VideoCodecCatalog>? RegisterAdditionalCodecs { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Start each run with a fresh debug log so users can find only the
            // current session's diagnostic data at DebugLog.FilePath. This
            // MUST happen before any component writes to the log, otherwise
            // the first Reset wipes whatever we already logged — which is
            // how we lost the FFmpeg init diagnostics at one point.
            DebugLog.Reset();
            DebugLog.Write($"== ScreenSharing client start @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==");

            // Platform host gets a chance to populate the catalog with
            // additional codec factories now that the log is ready to
            // receive diagnostic output from FFmpeg / MediaFoundation probes.
            if (VideoCodecCatalog is not null)
            {
                RegisterAdditionalCodecs?.Invoke(VideoCodecCatalog);
            }

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
