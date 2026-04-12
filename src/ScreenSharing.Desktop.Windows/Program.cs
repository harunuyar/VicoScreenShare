using System;
using Avalonia;
using ScreenSharing.Client;
using ScreenSharing.Client.Windows.Capture;

namespace ScreenSharing.Desktop.Windows;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Wire the Windows-specific capture backend into the platform-neutral
        // Client project before Avalonia starts. The factory receives a callback
        // that returns the current main window HWND and builds a
        // WindowsCaptureProvider parented to it.
        App.CaptureProviderFactory = hwndProvider => new WindowsCaptureProvider(hwndProvider);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, used by visual designer as well.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
