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
    private double _volume = 1.0;
    private short[] _scaledScratch = Array.Empty<short>();
    private int _state; // 0 = idle, 1 = running, 2 = disposed

    public int SampleRate => _sampleRate;

    public int Channels => _channels;

    /// <inheritdoc />
    /// <remarks>
    /// Volume is applied as a software multiplier on the PCM samples
    /// inside <see cref="Submit"/>, NOT via
    /// <c>WasapiOut.Volume</c> / AudioStreamVolume. The WASAPI
    /// stream-volume path behaves inconsistently across drivers (on
    /// some configurations it acts like an absolute level rather than
    /// a multiplier on top of the session + system volume), so a
    /// slider at 100% could play louder than the user's system volume
    /// would otherwise permit. Software scaling on S16 samples is a
    /// few instructions per frame and guarantees the per-tile slider
    /// behaves the same on every machine.
    /// </remarks>
    public double Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(value, 0.0, 1.0));
    }

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

        // Apply the per-tile volume as a software multiplier on S16
        // samples. A straight bit copy at volume == 1 keeps the cheap
        // path fast for the default "full volume" case.
        var volume = Volatile.Read(ref _volume);
        if (volume >= 0.999)
        {
            var byteSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleavedPcm);
            byteSpan.CopyTo(_scratch);
        }
        else if (volume <= 0.0)
        {
            // Silence — zero the scratch region we're about to submit.
            Array.Clear(_scratch, 0, needed);
        }
        else
        {
            if (_scaledScratch.Length < interleavedPcm.Length)
            {
                _scaledScratch = new short[interleavedPcm.Length];
            }
            // Saturate on clamp to avoid signed-overflow wrap on the
            // unlikely edge of a sample already near ±32768.
            for (var i = 0; i < interleavedPcm.Length; i++)
            {
                var scaled = interleavedPcm[i] * volume;
                if (scaled > short.MaxValue)
                {
                    _scaledScratch[i] = short.MaxValue;
                }
                else if (scaled < short.MinValue)
                {
                    _scaledScratch[i] = short.MinValue;
                }
                else
                {
                    _scaledScratch[i] = (short)scaled;
                }
            }
            var byteSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(_scaledScratch.AsSpan(0, interleavedPcm.Length));
            byteSpan.CopyTo(_scratch);
        }

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
