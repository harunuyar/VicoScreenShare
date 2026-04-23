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
    /// <para>Retained for the capture-test view which still uses the OS picker;
    /// the main share flow goes through
    /// <see cref="CreateSourceForTargetAsync"/> against a target already picked
    /// by the custom in-app picker.</para>
    /// </summary>
    Task<ICaptureSource?> PickSourceAsync(int targetFrameRate);

    /// <summary>
    /// Build a capture source for a target the caller has already
    /// chosen (through the custom picker). The provider dispatches on
    /// <see cref="CaptureTarget.Kind"/> to the right OS API —
    /// <c>CreateForWindow</c> on Windows for <see cref="CaptureTargetKind.Window"/>,
    /// <c>CreateForMonitor</c> for monitors. Returns null if the target
    /// has disappeared between enumeration and this call.
    /// <para>
    /// <paramref name="maxPreviewDimension"/> caps the capture size per
    /// side. Zero means "capture at source native resolution" (the
    /// publisher path). A small positive value (e.g. 320) asks the OS
    /// to downscale during capture — used by the share picker so
    /// 7-plus simultaneous thumbnails don't each burn 2560×1440 worth
    /// of GPU work per frame and so small tiles display with the
    /// compositor's proper downscaling filter instead of a brute-force
    /// bilinear at display time.
    /// </para>
    /// </summary>
    Task<ICaptureSource?> CreateSourceForTargetAsync(CaptureTarget target, int targetFrameRate, int maxPreviewDimension = 0);
}
