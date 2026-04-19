namespace VicoScreenShare.Client.Platform;

using System.Threading.Tasks;

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
    /// cancelled the picker. <paramref name="targetFrameRate"/> is the cadence
    /// the source's pace thread will dispatch frames at, regardless of how fast
    /// the underlying OS API delivers raw captures.
    /// </summary>
    Task<ICaptureSource?> PickSourceAsync(int targetFrameRate);
}
