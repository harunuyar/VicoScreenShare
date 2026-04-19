namespace VicoScreenShare.Client;

using System;
using VicoScreenShare.Client.Media.Codecs;
using VicoScreenShare.Client.Platform;

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
}
