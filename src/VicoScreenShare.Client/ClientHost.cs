namespace VicoScreenShare.Client;

using System;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Media;

/// <summary>
/// Process-global bag of host-provided factories. Populated by the
/// WinUI entry point in <c>VicoScreenShare.Desktop.WinUI.Program</c>
/// before the first XAML page is constructed, and read by view models
/// when they need a capture provider or a codec factory.
///
/// Static because the alternatives (a DI container, hand-plumbed
/// constructor chains) don't buy us much for a single-window app and
/// make the hot paths harder to reason about. There's one process, one
/// capture device, one codec catalog; a static is the honest shape.
/// </summary>
public static class ClientHost
{
    /// <summary>
    /// Builds an <see cref="ICaptureProvider"/> bound to a specific Win32
    /// HWND. Receives a function that returns the current main window
    /// handle on demand (nullable because the window may not exist at
    /// the moment the factory is first invoked).
    /// </summary>
    public static Func<Func<IntPtr>, ICaptureProvider>? CaptureProviderFactory { get; set; }

    /// <summary>
    /// Codec catalog populated at startup with whatever codecs this host
    /// can actually run — VP8 is baked in, H.264 is registered when
    /// Media Foundation initializes successfully.
    /// </summary>
    public static VideoCodecCatalog? VideoCodecCatalog { get; set; }

    /// <summary>
    /// Hook so the host can register additional codec factories AFTER
    /// the debug log has been reset. Any log lines written during codec
    /// probing survive into the session's log file instead of being
    /// wiped by an early <see cref="Diagnostics.DebugLog.Reset"/>.
    /// </summary>
    public static Action<VideoCodecCatalog>? RegisterAdditionalCodecs { get; set; }

    /// <summary>
    /// Capture-target enumerator used by the custom share picker to
    /// list windows and monitors. Null when the host has no such
    /// backend (test harnesses); the share picker treats null as "no
    /// targets" and cancels gracefully.
    /// </summary>
    public static ICaptureTargetEnumerator? CaptureTargetEnumerator { get; set; }

    /// <summary>
    /// System-audio loopback capture provider. Null when the host has
    /// no audio backend (headless test, Linux port until WASAPI is
    /// replaced). <see cref="ViewModels.RoomViewModel"/> reads this to
    /// decide whether to offer shared-content audio.
    /// </summary>
    public static IAudioCaptureProvider? AudioCaptureProvider { get; set; }

    /// <summary>Shared factory for Opus encoders (Concentus). Default is
    /// <see cref="OpusAudioCodecFactory"/>; tests can swap in a fake.</summary>
    public static IAudioEncoderFactory AudioEncoderFactory { get; set; } = new OpusAudioCodecFactory();

    /// <summary>Shared factory for Opus decoders. Same default / swap
    /// semantics as the encoder factory; the single instance is
    /// thread-safe because each <see cref="IAudioDecoderFactory.CreateDecoder"/>
    /// yields a fresh decoder.</summary>
    public static IAudioDecoderFactory AudioDecoderFactory { get; set; } = new OpusAudioCodecFactory();

    /// <summary>Factory for the WASAPI-backed audio resampler (or a
    /// pass-through for tests). Called once per publisher session.</summary>
    public static Func<IAudioResampler>? AudioResamplerFactory { get; set; }

    /// <summary>Factory for a per-viewer <see cref="IAudioRenderer"/>.
    /// Each <see cref="ViewModels.SubscriberSession"/> gets its own
    /// renderer instance; NAudio handles mixing multiple outputs at
    /// the system-mixer layer.</summary>
    public static Func<IAudioRenderer>? AudioRendererFactory { get; set; }
}
