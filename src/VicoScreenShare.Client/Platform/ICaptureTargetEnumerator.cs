namespace VicoScreenShare.Client.Platform;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Enumerates shareable capture targets (windows and monitors) on the
/// current host and fetches on-demand thumbnails for the picker dialog.
/// Windows implementation walks <c>EnumWindows</c> / <c>EnumDisplayMonitors</c>
/// with visibility filters matching industry norms (Discord / OBS
/// behaviors); a future Linux port would wrap the X11 / Wayland
/// equivalent.
/// <para>
/// Kept separate from <see cref="ICaptureProvider"/> so callers that
/// only need a target list (for a standalone picker UI) don't have to
/// materialize a full capture session.
/// </para>
/// </summary>
public interface ICaptureTargetEnumerator
{
    /// <summary>
    /// Return the current snapshot of shareable targets. Order is
    /// implementation-defined but stable across calls within a single
    /// window configuration (no shuffle between re-enumerations), so
    /// the picker dialog can refresh without the user's focus jumping.
    /// </summary>
    Task<IReadOnlyList<CaptureTarget>> EnumerateAsync(CancellationToken ct = default);

    /// <summary>
    /// Capture a static thumbnail of the target at approximately the
    /// requested pixel size. Implementations may return a smaller image
    /// when the target is smaller than the request (no upscaling).
    /// Returns null when the target has gone away between enumeration
    /// and thumbnail fetch (window closed, monitor disconnected).
    /// </summary>
    Task<CaptureTargetImage?> GetThumbnailAsync(CaptureTarget target, int maxWidth, int maxHeight, CancellationToken ct = default);
}
