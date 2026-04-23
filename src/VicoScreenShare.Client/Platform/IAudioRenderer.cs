namespace VicoScreenShare.Client.Platform;

using System;
using System.Threading.Tasks;

/// <summary>
/// Plays PCM out to a local audio endpoint. The viewer's
/// <c>AudioReceiver</c> submits decoded Opus frames (16-bit signed
/// interleaved) into the renderer, which internally buffers a small
/// amount (~200 ms) and hands it to WASAPI.
/// <para>
/// Rendering happens on an OS-owned mixer thread, so <see cref="Submit"/>
/// is non-blocking and cannot throw into the receiver's decode hot path —
/// any errors are swallowed and logged by the implementation. Starve
/// protection is WASAPI's job (the default loopback buffer is ~10 ms and
/// any underflow produces silence, which is the right behavior when the
/// network dries up).
/// </para>
/// </summary>
public interface IAudioRenderer : IAsyncDisposable
{
    /// <summary>Sample rate the renderer is running at, or 0 before
    /// <see cref="StartAsync"/>.</summary>
    int SampleRate { get; }

    /// <summary>Interleaved channel count the renderer is running at,
    /// or 0 before start.</summary>
    int Channels { get; }

    /// <summary>
    /// Open the render endpoint and begin consuming audio. The renderer
    /// converts between its internal buffer format and the device's
    /// native mix format (WASAPI shared mode); sample rate / channel
    /// count mismatches are handled transparently. Safe to call multiple
    /// times with the same parameters — subsequent calls are no-ops.
    /// </summary>
    Task StartAsync(int sampleRate, int channels);

    /// <summary>
    /// Push decoded PCM into the renderer's buffer. Non-blocking; drops
    /// samples silently if the buffer is near capacity (we prefer a
    /// silent moment over growing a latency backlog the viewer can't
    /// shed). The <paramref name="timestamp"/> is the RTP content
    /// timestamp of the submitted samples — carried for diagnostic
    /// logging only, not for scheduling (WASAPI clocks itself).
    /// </summary>
    void Submit(ReadOnlySpan<short> interleavedPcm, TimeSpan timestamp);

    Task StopAsync();
}
