using System;
using System.Threading.Tasks;

namespace ScreenSharing.Client.Platform;

/// <summary>
/// Handler for captured frames. The span is valid only for the duration of the call;
/// subscribers must copy bytes they intend to keep. Invoked on whatever thread the
/// capture backend is producing frames on — not guaranteed to be the UI thread.
/// </summary>
public delegate void FrameArrivedHandler(in CaptureFrameData frame);

/// <summary>
/// A live capture of a single source (window or monitor). Produced by
/// <see cref="ICaptureProvider.PickSourceAsync"/>. Callers own the instance and
/// must dispose it when they are finished capturing.
/// </summary>
public interface ICaptureSource : IAsyncDisposable
{
    /// <summary>Human-readable label for the captured source (window title, monitor name).</summary>
    string DisplayName { get; }

    /// <summary>
    /// Raised every time the backend produces a frame. Subscribers must copy any
    /// data they need before the handler returns.
    /// </summary>
    event FrameArrivedHandler? FrameArrived;

    /// <summary>
    /// Raised when the backend loses access to the source (the shared window was
    /// closed, the user revoked capture, the monitor disconnected). After this
    /// event <see cref="FrameArrived"/> will no longer fire.
    /// </summary>
    event Action? Closed;

    Task StartAsync();

    Task StopAsync();
}
