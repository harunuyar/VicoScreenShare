namespace VicoScreenShare.Client.Windows.Capture;

using System;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using VicoScreenShare.Client.Platform;
using VicoScreenShare.Client.Windows.Audio;

/// <summary>
/// Windows implementation of <see cref="IAudioCaptureProvider"/>. Builds a
/// loopback source bound to the system's default render endpoint (the
/// "Console" role, which is the same endpoint the user hears through
/// their default output). If no render endpoint is active the provider
/// returns null rather than throwing — an app running on a VM without an
/// audio device, or a machine with all endpoints disabled, should still
/// be able to share video silently.
/// </summary>
public sealed class WasapiAudioCaptureProvider : IAudioCaptureProvider
{
    public Task<IAudioCaptureSource?> CreateLoopbackSourceAsync()
    {
        MMDevice? device;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
            {
                return Task.FromResult<IAudioCaptureSource?>(null);
            }
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }
        catch
        {
            // Audio service disabled, policy-blocked, driver fault:
            // caller treats null as "no audio available" and continues
            // with a silent share.
            return Task.FromResult<IAudioCaptureSource?>(null);
        }

        // WasapiLoopbackAudioSource owns the MMDevice handle from this
        // point on; it disposes the capture's internal device reference
        // when disposed. The variable-name shadow matches the MMDevice
        // ownership semantics elsewhere in NAudio.
        IAudioCaptureSource source = new WasapiLoopbackAudioSource(device);
        return Task.FromResult<IAudioCaptureSource?>(source);
    }

    public Task<IAudioCaptureSource?> CreateProcessLoopbackSourceAsync(int processId)
    {
        if (processId <= 0)
        {
            return Task.FromResult<IAudioCaptureSource?>(null);
        }
        IAudioCaptureSource source;
        try
        {
            source = new ProcessLoopbackAudioSource(processId);
        }
        catch
        {
            // Invalid PID (process gone between picker and here): caller
            // treats null as "fall back to silent share" — better than
            // tearing down the whole share flow for a race condition.
            return Task.FromResult<IAudioCaptureSource?>(null);
        }
        return Task.FromResult<IAudioCaptureSource?>(source);
    }
}
