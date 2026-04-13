using System;
using System.Reflection;
using Avalonia;
using ScreenSharing.Client;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using ScreenSharing.Client.Windows.Capture;
using ScreenSharing.Client.Windows.Direct3D;
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
        // One D3D11 device for the whole session — shared between the WGC
        // framepool (capture) and the Media Foundation H.264 encoder. This
        // is what makes zero-copy texture handoff possible: capture source,
        // GPU scaler, and encoder all see the same device, so a framepool
        // surface can be blt'd straight into the encoder's input texture
        // with no cross-device copies.
        var sharedDevices = new D3D11DeviceManager();
        sharedDevices.Initialize();

        App.CaptureProviderFactory = hwndProvider => new WindowsCaptureProvider(hwndProvider, sharedDevices);

        // Codec catalog wiring. VP8 is baked into VideoCodecCatalog's ctor so
        // the app always has a working codec even before Avalonia starts.
        // H.264 via Media Foundation is registered later, after Avalonia
        // resets the debug log, so any probe diagnostics survive in the log.
        App.VideoCodecCatalog = new VideoCodecCatalog();
        App.RegisterAdditionalCodecs = catalog => RegisterHardwareCodecs(catalog, sharedDevices);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void TryInstallHighFpsRenderTimer(int fps)
    {
        try
        {
            var avaloniaBase = typeof(Avalonia.Application).Assembly;
            var locatorType = avaloniaBase.GetType("Avalonia.AvaloniaLocator");
            var timerType = avaloniaBase.GetType("Avalonia.Rendering.DefaultRenderTimer");
            var iRenderTimer = avaloniaBase.GetType("Avalonia.Rendering.IRenderTimer");
            if (locatorType is null || timerType is null || iRenderTimer is null)
            {
                DebugLog.Write("[render] could not resolve Avalonia render-timer types");
                return;
            }

            var timerCtor = timerType.GetConstructor(new[] { typeof(int) });
            if (timerCtor is null)
            {
                DebugLog.Write("[render] DefaultRenderTimer(int) ctor not found");
                return;
            }
            var timer = timerCtor.Invoke(new object[] { fps });

            var currentMutable = locatorType.GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            if (currentMutable is null)
            {
                DebugLog.Write("[render] AvaloniaLocator.CurrentMutable not found");
                return;
            }
            var bindMethod = locatorType.GetMethod("Bind", BindingFlags.Public | BindingFlags.Instance);
            if (bindMethod is null)
            {
                DebugLog.Write("[render] AvaloniaLocator.Bind<T>() not found");
                return;
            }
            var helper = bindMethod.MakeGenericMethod(iRenderTimer).Invoke(currentMutable, null);
            if (helper is null)
            {
                DebugLog.Write("[render] Bind<IRenderTimer>() returned null");
                return;
            }
            var toConstant = helper.GetType().GetMethod("ToConstant");
            if (toConstant is null)
            {
                DebugLog.Write("[render] RegistrationHelper.ToConstant not found");
                return;
            }
            toConstant.Invoke(helper, new[] { timer });
            DebugLog.Write($"[render] installed DefaultRenderTimer({fps}) — paint rate uncapped from 60 fps default");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[render] TryInstallHighFpsRenderTimer threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RegisterHardwareCodecs(VideoCodecCatalog catalog, D3D11DeviceManager sharedDevices)
    {
        MediaFoundationRuntime.EnsureInitialized();
        if (MediaFoundationRuntime.IsAvailable)
        {
            // The encoder is built against the same D3D11 device as the
            // capture framepool so texture handoff doesn't need shared
            // handles or cross-device copies.
            catalog.Register(
                new MediaFoundationH264EncoderFactory(sharedDevices.Device),
                new MediaFoundationH264DecoderFactory(sharedDevices.Device));
        }
    }

    // Avalonia configuration, used by visual designer as well.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            // Avalonia's default IRenderTimer ticks at 60 fps on Win32
            // regardless of the display refresh rate — at 120+ fps video
            // input that's what caps paint fps at 60 even though the
            // decoder is producing frames faster. Replace it with a
            // 240 fps timer so receiver tiles can actually repaint at
            // the display refresh rate of modern gaming monitors.
            //
            // AvaloniaLocator.CurrentMutable and DefaultRenderTimer's
            // int constructor are internal in the reference assembly
            // but public at runtime, so we reach them via reflection.
            // This runs after platform services but before the first
            // window opens, which is when Avalonia first reads the
            // IRenderTimer service.
            .AfterPlatformServicesSetup(_ => TryInstallHighFpsRenderTimer(240))
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
