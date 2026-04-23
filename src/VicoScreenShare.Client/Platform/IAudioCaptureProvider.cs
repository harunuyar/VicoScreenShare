namespace VicoScreenShare.Client.Platform;

using System.Threading.Tasks;

/// <summary>
/// Platform-neutral entry point for shared-content audio capture. The
/// Windows implementation wraps WASAPI loopback against the default
/// render endpoint; a future Linux port would wrap PipeWire / PulseAudio
/// monitor sources. Registered into view models at app startup so the
/// WPF client never has to import a platform-specific audio backend
/// directly.
/// <para>
/// There is no interactive picker today — system-audio sharing goes to
/// whatever is playing out of the default render endpoint. A future UI
/// can enumerate endpoints and pick a specific one, at which point this
/// interface gains a <c>PickEndpointAsync</c> companion.
/// </para>
/// </summary>
public interface IAudioCaptureProvider
{
    /// <summary>
    /// Build a capture source bound to the system's default render
    /// endpoint (loopback). Returns null when no active render endpoint
    /// is present — a machine with audio disabled or all endpoints
    /// disconnected. Callers should treat that as "silent share" rather
    /// than an error.
    /// </summary>
    Task<IAudioCaptureSource?> CreateLoopbackSourceAsync();

    /// <summary>
    /// Build a capture source scoped to a single process tree via the
    /// Windows 10 2004+ process-loopback API. Used when the user shares
    /// a specific window — audio from ONLY that app (and any children
    /// it spawned) crosses the wire, not the whole system mix. Returns
    /// null when the platform doesn't support process loopback (older
    /// Windows builds, non-Windows ports).
    /// </summary>
    Task<IAudioCaptureSource?> CreateProcessLoopbackSourceAsync(int processId);
}
