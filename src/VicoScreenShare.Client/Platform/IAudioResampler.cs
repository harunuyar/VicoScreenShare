namespace VicoScreenShare.Client.Platform;

using System;

/// <summary>
/// Converts captured PCM (whatever format the audio engine is running in)
/// to the codec's expected format (Opus: 48 kHz, 16-bit signed,
/// interleaved). Shared-mode WASAPI on modern Windows is almost always
/// 48 kHz IEEE float, so the resampler's common case reduces to an
/// F32→S16 conversion with no rate change. When the device mix is at
/// 44.1 kHz (some Bluetooth devices, some hi-fi DACs) the implementation
/// runs a proper sample-rate conversion.
/// <para>
/// Lives in the platform-neutral project so the publisher orchestrator
/// can call it without a Windows reference; the Windows project supplies
/// the NAudio-backed implementation via DI.
/// </para>
/// </summary>
public interface IAudioResampler : IDisposable
{
    /// <summary>
    /// Convert a buffer of input PCM to 48 kHz interleaved S16, writing
    /// into the caller-supplied <paramref name="destination"/> and
    /// returning the number of shorts written. Implementations must
    /// handle any combination of input format (F32 or S16) and input
    /// sample rate, outputting the same channel count as the input.
    /// <para>
    /// If <paramref name="destination"/> is too small the implementation
    /// throws <see cref="ArgumentException"/>; the caller is responsible
    /// for sizing it correctly. A safe upper bound is:
    /// <c>ceil(inputSamples × 48000 / inputSampleRate) × channels</c>
    /// plus a small slack for resampler edge rounding.
    /// </para>
    /// </summary>
    int Resample(
        ReadOnlySpan<byte> inputPcm,
        int inputSampleRate,
        int inputChannels,
        AudioSampleFormat inputFormat,
        Span<short> destination);
}
