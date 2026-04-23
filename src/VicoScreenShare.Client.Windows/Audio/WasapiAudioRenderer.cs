namespace VicoScreenShare.Client.Windows.Audio;

using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VicoScreenShare.Client.Platform;

/// <summary>
/// <see cref="IAudioRenderer"/> backed by NAudio's
/// <see cref="WasapiOut"/>. Opens the default render endpoint in shared
/// mode and plays interleaved S16 PCM pushed via
/// <see cref="Submit"/>. WASAPI handles any sample-rate / bit-depth
/// conversion against the endpoint's mix format internally.
/// <para>
/// The renderer owns a small bounded <see cref="BufferedWaveProvider"/>
/// (~200 ms). On overflow it drops the oldest samples — we prefer a
/// momentary audio glitch over growing a latency backlog that would
/// permanently desync with the video stream. Underflow produces silence
/// naturally (the WASAPI mixer substitutes zeros when the buffer empties).
/// </para>
/// </summary>
public sealed class WasapiAudioRenderer : IAudioRenderer
{
    private readonly object _lifecycleLock = new();

    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private byte[] _scratch = Array.Empty<byte>();
    private int _sampleRate;
    private int _channels;
    private int _state; // 0 = idle, 1 = running, 2 = disposed

    public int SampleRate => _sampleRate;

    public int Channels => _channels;

    public Task StartAsync(int sampleRate, int channels)
    {
        if (sampleRate <= 0 || channels <= 0)
        {
            throw new ArgumentException("Sample rate and channels must be positive.");
        }

        lock (_lifecycleLock)
        {
            if (_state == 2)
            {
                throw new ObjectDisposedException(nameof(WasapiAudioRenderer));
            }
            if (_state == 1)
            {
                if (_sampleRate == sampleRate && _channels == channels)
                {
                    return Task.CompletedTask;
                }
                // Different target — rebuild. Callers normally stick
                // with 48 kHz / stereo; a format change implies the
                // publisher moved to mono mid-session or similar.
                StopInternal();
            }

            var waveFormat = new WaveFormat(sampleRate, 16, channels);
            var buffer = new BufferedWaveProvider(waveFormat)
            {
                // 200 ms is the sync tolerance per WebRTC lip-sync spec.
                BufferDuration = TimeSpan.FromMilliseconds(200),
                DiscardOnBufferOverflow = true,
            };
            // Shared mode; 50 ms WASAPI-side latency. The actual
            // end-to-end render latency is approximately (our buffer) +
            // (WASAPI latency) + (driver + hardware ring). 200 ms + 50 ms
            // is comfortable without being sluggish.
            var output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 50);
            output.Init(buffer);
            output.Play();

            _output = output;
            _buffer = buffer;
            _sampleRate = sampleRate;
            _channels = channels;
            Interlocked.Exchange(ref _state, 1);
        }
        return Task.CompletedTask;
    }

    public void Submit(ReadOnlySpan<short> interleavedPcm, TimeSpan timestamp)
    {
        if (_state != 1 || interleavedPcm.IsEmpty)
        {
            return;
        }

        // BufferedWaveProvider consumes a byte[] because its IWaveProvider
        // contract is byte-oriented. Keep a reusable scratch buffer
        // sized to the frame to stay off the allocation path per submit.
        var needed = interleavedPcm.Length * sizeof(short);
        if (_scratch.Length < needed)
        {
            _scratch = new byte[needed];
        }
        var byteSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleavedPcm);
        byteSpan.CopyTo(_scratch);

        try
        {
            _buffer?.AddSamples(_scratch, 0, needed);
        }
        catch
        {
            // BufferedWaveProvider throws InvalidOperationException on
            // true overflow (when DiscardOnBufferOverflow is false). We
            // enable discard-on-overflow so this path shouldn't fire,
            // but swallow regardless — never fault the decoder thread.
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            if (_state != 1)
            {
                return Task.CompletedTask;
            }
            StopInternal();
            Interlocked.Exchange(ref _state, 0);
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_state == 2)
            {
                return ValueTask.CompletedTask;
            }
            StopInternal();
            Interlocked.Exchange(ref _state, 2);
        }
        return ValueTask.CompletedTask;
    }

    private void StopInternal()
    {
        var output = _output;
        _output = null;
        _buffer = null;
        if (output is not null)
        {
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
        }
    }
}
