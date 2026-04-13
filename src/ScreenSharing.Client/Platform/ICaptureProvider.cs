using System.Threading.Tasks;

namespace ScreenSharing.Client.Platform;

/// <summary>
/// Platform-neutral entry point for screen / window capture. The Windows
/// implementation wraps <c>Windows.Graphics.Capture</c>; a future Linux
/// implementation can wrap PipeWire or XComposite. Registered into view models
/// at app startup so the Avalonia client project never has to import a
/// platform-specific capture backend directly.
/// </summary>
public interface ICaptureProvider
{
    /// <summary>
    /// Show the system picker and let the user pick a window or monitor. Returns
    /// an <see cref="ICaptureSource"/> ready to start, or <c>null</c> if the user
    /// cancelled the picker.
    /// </summary>
    Task<ICaptureSource?> PickSourceAsync();

    /// <summary>
    /// Create a capture source for the primary screen without going through the
    /// system picker. On Windows this is backed by DXGI Desktop Duplication —
    /// which, unlike <c>Windows.Graphics.Capture</c>, bypasses DWM's idle-window
    /// compose throttling and sustains the monitor's native refresh rate even
    /// when the user's cursor is stationary. This is the preferred backend for
    /// the "Share Screen" button; <see cref="PickSourceAsync"/> remains for
    /// single-window sharing.
    /// </summary>
    Task<ICaptureSource?> PickScreenAsync();
}
