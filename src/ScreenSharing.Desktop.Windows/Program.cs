using System;
using Avalonia;
using ScreenSharing.Client;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Windows.Capture;
using ScreenSharing.Client.Windows.Media.Codecs;

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

        // Codec catalog wiring. VP8 is baked into VideoCodecCatalog's ctor so
        // the app always has at least one working codec even before Avalonia
        // starts up. H.264 registration happens later, inside
        // App.OnFrameworkInitializationCompleted, so the DebugLog.Reset in
        // that path does not wipe our "[ffmpeg] initialized from ..." lines.
        App.VideoCodecCatalog = new VideoCodecCatalog();
        App.RegisterAdditionalCodecs = RegisterFFmpegCodecsIfAvailable;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RegisterFFmpegCodecsIfAvailable(VideoCodecCatalog catalog)
    {
        FFmpegRuntime.EnsureInitialized();
        if (FFmpegRuntime.IsAvailable)
        {
            catalog.Register(new FFmpegH264EncoderFactory(), new FFmpegH264DecoderFactory());
        }
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
