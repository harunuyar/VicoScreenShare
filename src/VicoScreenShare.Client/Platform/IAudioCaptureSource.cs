namespace VicoScreenShare.Client.Platform;

using System;
using System.Threading.Tasks;

/// <summary>
/// Handler for captured audio buffers. The span is valid only for the
/// duration of the call; subscribers must copy bytes they intend to keep.
/// Invoked on whatever thread the WASAPI backend is producing buffers on —
/// not the UI thread. Mirrors <see cref="FrameArrivedHandler"/> for audio.
/// </summary>
public delegate void AudioFrameArrivedHandler(in AudioFrameData frame);

/// <summary>
/// A live capture of a single audio source (system loopback in the
/// initial implementation). Produced by
/// <see cref="IAudioCaptureProvider.CreateLoopbackSourceAsync"/>. Callers
/// own the instance and must dispose it when they are finished.
/// <para>
/// The source delivers raw PCM in whatever format the underlying audio
/// engine is running in (WASAPI shared mode on Windows: typically IEEE
/// float 48 kHz stereo). Callers resample / convert via an
/// <c>IAudioResampler</c> before handing the data to a codec.
/// </para>
/// </summary>
public interface IAudioCaptureSource : IAsyncDisposable
{
    /// <summary>Human-readable label for the captured endpoint
    /// (friendly device name).</summary>
    string DisplayName { get; }

    /// <summary>Sample rate the source is currently producing, or 0
    /// before <see cref="StartAsync"/> resolves the device's mix
    /// format.</summary>
    int SourceSampleRate { get; }

    /// <summary>Channel count the source is currently producing, or 0
    /// before start.</summary>
    int SourceChannels { get; }

    /// <summary>Sample format the source is currently producing.</summary>
    AudioSampleFormat SourceFormat { get; }

    /// <summary>
    /// Raised every time the backend produces a buffer. Subscribers must
    /// copy any data they need before the handler returns.
    /// </summary>
    event AudioFrameArrivedHandler? FrameArrived;

    /// <summary>
    /// Raised when the backend loses access to the endpoint (device
    /// unplugged, audio engine reset). After this event
    /// <see cref="FrameArrived"/> will no longer fire.
    /// </summary>
    event Action? Closed;

    Task StartAsync();

    Task StopAsync();
}
