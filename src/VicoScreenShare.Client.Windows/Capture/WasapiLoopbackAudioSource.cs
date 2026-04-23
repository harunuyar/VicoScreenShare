namespace VicoScreenShare.Client.Windows.Capture;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VicoScreenShare.Client.Platform;

/// <summary>
/// <see cref="IAudioCaptureSource"/> backed by NAudio's
/// <see cref="WasapiLoopbackCapture"/>. Captures the bit-stream that the
/// default (or user-selected) render endpoint is playing back — i.e.
/// whatever the user hears through their speakers / headphones.
/// <para>
/// WASAPI shared-mode loopback runs at the endpoint's native mix format;
/// on Windows 10+ that is almost always 48 kHz IEEE float stereo, which
/// is exactly what Opus consumes after a cheap F32→S16 conversion. The
/// source does not perform that conversion itself — it reports the native
/// format via <see cref="SourceSampleRate"/> / <see cref="SourceChannels"/>
/// / <see cref="SourceFormat"/> and hands raw bytes to subscribers, who
/// run them through an <see cref="IAudioResampler"/> on their own thread.
/// </para>
/// <para>
/// Timestamps are monotonic and derived from a <see cref="Stopwatch"/>
/// started at <see cref="StartAsync"/>. That puts them on the same clock
/// base as <c>Stopwatch.GetTimestamp</c> (QueryPerformanceCounter), which
/// is the same base <see cref="System.Diagnostics.Stopwatch"/>-fed
/// timestamps throughout the video path use. Without that alignment, the
/// publisher's RTCP SR on each track would reference different wall clocks
/// and the viewer's lip sync would drift.
/// </para>
/// </summary>
public sealed class WasapiLoopbackAudioSource : IAudioCaptureSource
{
    private readonly MMDevice? _device;
    private readonly string _displayName;
    private readonly object _lifecycleLock = new();

    private WasapiLoopbackCapture? _capture;
    private Stopwatch? _clock;
    private int _sourceSampleRate;
    private int _sourceChannels;
    private AudioSampleFormat _sourceFormat;
    private int _state; // 0 = idle, 1 = running, 2 = disposed

    public WasapiLoopbackAudioSource(MMDevice? device = null)
    {
        // A null device means "use the default render endpoint" — NAudio's
        // parameterless WasapiLoopbackCapture ctor handles that path.
        _device = device;
        _displayName = device?.FriendlyName ?? "Default render endpoint";
    }

    public string DisplayName => _displayName;

    public int SourceSampleRate => _sourceSampleRate;

    public int SourceChannels => _sourceChannels;

    public AudioSampleFormat SourceFormat => _sourceFormat;

    public event AudioFrameArrivedHandler? FrameArrived;

    public event Action? Closed;

    public Task StartAsync()
    {
        lock (_lifecycleLock)
        {
            if (_state == 2)
            {
                throw new ObjectDisposedException(nameof(WasapiLoopbackAudioSource));
            }
            if (_state == 1)
            {
                return Task.CompletedTask;
            }

            var capture = _device is null
                ? new WasapiLoopbackCapture()
                : new WasapiLoopbackCapture(_device);

            var fmt = capture.WaveFormat;
            // WASAPI shared mode reports the device's current mix format.
            // Map it to our platform-neutral AudioSampleFormat. If the
            // encoding is neither IEEE float nor PCM, throw — we'd have
            // to synthesize a full conversion path for a codec nobody's
            // actually running and that is out of scope for first cut.
            _sourceFormat = fmt.Encoding switch
            {
                WaveFormatEncoding.IeeeFloat => AudioSampleFormat.PcmF32Interleaved,
                WaveFormatEncoding.Pcm when fmt.BitsPerSample == 16 => AudioSampleFormat.PcmS16Interleaved,
                WaveFormatEncoding.Extensible when fmt.BitsPerSample == 32 => AudioSampleFormat.PcmF32Interleaved,
                WaveFormatEncoding.Extensible when fmt.BitsPerSample == 16 => AudioSampleFormat.PcmS16Interleaved,
                _ => throw new NotSupportedException(
                    $"WASAPI loopback endpoint exposes an unsupported format: {fmt.Encoding} " +
                    $"{fmt.BitsPerSample}-bit {fmt.SampleRate} Hz {fmt.Channels}ch. " +
                    "First-cut shared-audio supports IEEE float or 16-bit PCM only."),
            };
            _sourceSampleRate = fmt.SampleRate;
            _sourceChannels = fmt.Channels;

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
            _clock = Stopwatch.StartNew();
            Interlocked.Exchange(ref _state, 1);

            try
            {
                capture.StartRecording();
            }
            catch
            {
                // StartRecording may fail if the endpoint was unplugged
                // between construction and start. Unwind cleanly so a
                // retry (with a new source) is possible.
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
                _capture = null;
                _clock = null;
                Interlocked.Exchange(ref _state, 0);
                throw;
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        WasapiLoopbackCapture? toStop = null;
        lock (_lifecycleLock)
        {
            if (_state != 1)
            {
                return Task.CompletedTask;
            }
            toStop = _capture;
            Interlocked.Exchange(ref _state, 0);
        }
        try
        {
            toStop?.StopRecording();
        }
        catch
        {
            // Best-effort — capture might already be in a stopped state
            // if the device died.
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // Flip to disposed first so any in-flight DataAvailable callback
        // that races with dispose short-circuits without calling into
        // FrameArrived subscribers against partially-torn-down state.
        WasapiLoopbackCapture? capture;
        lock (_lifecycleLock)
        {
            if (_state == 2)
            {
                return;
            }
            capture = _capture;
            _capture = null;
            Interlocked.Exchange(ref _state, 2);
        }

        if (capture is not null)
        {
            try { capture.StopRecording(); } catch { }
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.Dispose(); } catch { }
        }
        await Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_state != 1 || e.BytesRecorded <= 0)
        {
            return;
        }

        // WasapiLoopbackCapture fills a dead-period buffer with silence
        // when nothing is playing back. That is the correct semantic for
        // a live loopback source — the viewer should hear silence, not
        // drop out — so we forward all bytes regardless.
        var ts = _clock?.Elapsed ?? TimeSpan.Zero;
        var frame = new AudioFrameData(
            new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded),
            _sourceSampleRate,
            _sourceChannels,
            _sourceFormat,
            ts);

        try
        {
            FrameArrived?.Invoke(in frame);
        }
        catch
        {
            // A subscriber fault must not kill the capture thread. Log
            // via a future logger; silent swallow for now to match the
            // video-side hot-path convention.
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Fired when StopRecording is called OR the device reported an
        // error mid-stream. Distinguish by e.Exception: non-null means
        // the endpoint died and we raise Closed so the orchestrator can
        // tear down the streamer.
        if (e.Exception is null)
        {
            return;
        }
        try { Closed?.Invoke(); } catch { }
    }
}
