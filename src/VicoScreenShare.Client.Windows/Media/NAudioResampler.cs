namespace VicoScreenShare.Client.Windows.Media;

using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VicoScreenShare.Client.Platform;

/// <summary>
/// <see cref="IAudioResampler"/> implementation using NAudio's managed
/// <see cref="WdlResamplingSampleProvider"/>. Produces 48 kHz S16
/// interleaved output to feed Opus. The common case on modern Windows
/// (shared-mode WASAPI at 48 kHz IEEE float) reduces to a straight
/// F32→S16 conversion with no rate change; the rare 44.1 kHz or 96 kHz
/// device mix path runs a real resample.
/// <para>
/// Implementation is stateless per call: a new resampler is built for
/// each <see cref="Resample"/> invocation. That costs ~a few microseconds
/// and lets us avoid maintaining per-format state — the capture source
/// can change format mid-run (rare, but possible when the user switches
/// default devices) and the resampler has no per-session state to
/// corrupt.
/// </para>
/// </summary>
public sealed class NAudioResampler : IAudioResampler
{
    private const int TargetSampleRate = 48000;

    public int Resample(
        ReadOnlySpan<byte> inputPcm,
        int inputSampleRate,
        int inputChannels,
        AudioSampleFormat inputFormat,
        Span<short> destination)
    {
        if (inputPcm.IsEmpty)
        {
            return 0;
        }
        if (inputSampleRate <= 0 || inputChannels <= 0)
        {
            throw new ArgumentException("Input sample rate and channels must be positive.");
        }

        // Bytes per sample per channel.
        var inputBytesPerSample = inputFormat switch
        {
            AudioSampleFormat.PcmS16Interleaved => 2,
            AudioSampleFormat.PcmF32Interleaved => 4,
            _ => throw new NotSupportedException($"Unsupported input format {inputFormat}"),
        };

        var inputFrames = inputPcm.Length / (inputBytesPerSample * inputChannels);
        if (inputFrames == 0)
        {
            return 0;
        }

        // Materialize the input as a float[] because NAudio's
        // ISampleProvider surface is float-only. S16 input gets widened
        // here (cheap — a single divide per sample). F32 input gets a
        // byte→float reinterpret copy.
        var floatFrames = new float[inputFrames * inputChannels];
        if (inputFormat == AudioSampleFormat.PcmF32Interleaved)
        {
            // ReadOnlySpan<byte> of IEEE floats → float[]. Use a typed
            // reinterpret for the copy, not per-sample BitConverter, so
            // the JIT can vectorize.
            var floatSrc = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(inputPcm);
            floatSrc[..floatFrames.Length].CopyTo(floatFrames);
        }
        else
        {
            // S16 interleaved: widen to float in [-1, 1].
            var shortSrc = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(inputPcm);
            for (var i = 0; i < floatFrames.Length; i++)
            {
                floatFrames[i] = shortSrc[i] / 32768f;
            }
        }

        // Feed the float frames through a simple pass-through provider so
        // the WDL resampler can read them. WdlResamplingSampleProvider
        // asks the source for its own rate and converts to the configured
        // target.
        var source = new FloatArraySampleProvider(floatFrames, inputSampleRate, inputChannels);
        var resampler = new WdlResamplingSampleProvider(source, TargetSampleRate);

        // WDL will produce at most ceil(inputFrames × 48000 / sourceRate)
        // frames. Over-allocate the float output a little to absorb
        // edge-of-block rounding without requiring a second call.
        var estimatedOutFrames = (int)Math.Ceiling((double)inputFrames * TargetSampleRate / inputSampleRate) + 8;
        var outFloatBuf = new float[estimatedOutFrames * inputChannels];
        var produced = resampler.Read(outFloatBuf, 0, outFloatBuf.Length);

        var producedShorts = produced; // interleaved: samples already counts all channels
        if (destination.Length < producedShorts)
        {
            throw new ArgumentException(
                $"Resample destination too small: needed {producedShorts} shorts, got {destination.Length}.",
                nameof(destination));
        }

        // Float → S16 saturation. Standard clamp + round. 32767 not 32768
        // to keep the positive max inside int16 range.
        for (var i = 0; i < producedShorts; i++)
        {
            var f = outFloatBuf[i] * 32767f;
            if (f > 32767f) f = 32767f;
            else if (f < -32768f) f = -32768f;
            destination[i] = (short)f;
        }
        return producedShorts;
    }

    public void Dispose()
    {
        // Stateless: nothing to release.
    }

    /// <summary>
    /// Minimal <see cref="ISampleProvider"/> that yields a pre-filled
    /// float buffer once and then returns 0 (EOF) on subsequent reads.
    /// Good enough for a one-shot resample call; nothing in this path
    /// needs seeking or repeat reads.
    /// </summary>
    private sealed class FloatArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public FloatArraySampleProvider(float[] samples, int sampleRate, int channels)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var remaining = _samples.Length - _position;
            var take = Math.Min(remaining, count);
            if (take <= 0)
            {
                return 0;
            }
            Array.Copy(_samples, _position, buffer, offset, take);
            _position += take;
            return take;
        }
    }
}
