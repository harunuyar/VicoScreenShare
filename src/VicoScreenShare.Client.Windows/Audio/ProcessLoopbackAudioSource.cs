namespace VicoScreenShare.Client.Windows.Audio;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Platform;

/// <summary>
/// <see cref="IAudioCaptureSource"/> that captures audio from a single
/// process tree via the Windows 10 2004+ process-scoped loopback API
/// (<c>ActivateAudioInterfaceAsync</c> with
/// <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c> and the virtual
/// audio device path <c>VAD\Process_Loopback</c>). Unlike the default
/// render endpoint loopback, this sources ONLY the target PID's audio —
/// no notifications, no unrelated apps mixing in — which is exactly
/// what the share picker's per-window share needs.
/// <para>
/// Shape matches <see cref="Capture.WasapiLoopbackAudioSource"/>
/// identically on the outside so <c>AudioStreamer</c> consumes either
/// interchangeably. Internals differ: the activation is an async COM
/// operation that completes via a custom <c>IActivateAudioInterfaceCompletionHandler</c>;
/// the capture loop drives <c>IAudioCaptureClient</c> on a dedicated
/// thread (not NAudio's WasapiCapture which doesn't expose the
/// activation path).
/// </para>
/// </summary>
public sealed class ProcessLoopbackAudioSource : IAudioCaptureSource
{
    private const string VirtualDevicePath = "VAD\\Process_Loopback";
    private const int DefaultSampleRate = 48000;
    private const int DefaultChannels = 2;
    private const int BitsPerSampleF32 = 32;

    private readonly uint _processId;
    private readonly object _lifecycleLock = new();

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Thread? _captureThread;
    private CancellationTokenSource? _captureCts;
    private ManualResetEventSlim? _bufferEvent;
    private Stopwatch? _clock;
    private int _state; // 0 = idle, 1 = running, 2 = disposed

    public ProcessLoopbackAudioSource(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }
        _processId = (uint)processId;
        string name;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch
        {
            name = $"pid {processId}";
        }
        DisplayName = $"{name} (process loopback)";
    }

    public string DisplayName { get; }

    public int SourceSampleRate => DefaultSampleRate;

    public int SourceChannels => DefaultChannels;

    // The process-loopback API requires a PCM or IEEE float format we
    // pick ourselves. We ask for 48 kHz IEEE float stereo — matches
    // WasapiLoopbackCapture's typical output so the downstream
    // resampler / Opus stage takes the same pass-through path.
    public AudioSampleFormat SourceFormat => AudioSampleFormat.PcmF32Interleaved;

    public event AudioFrameArrivedHandler? FrameArrived;

    public event Action? Closed;

    public async Task StartAsync()
    {
        lock (_lifecycleLock)
        {
            if (_state == 2)
            {
                throw new ObjectDisposedException(nameof(ProcessLoopbackAudioSource));
            }
            if (_state == 1)
            {
                return;
            }
        }

        IAudioClient? audioClient;
        try
        {
            audioClient = await ActivateProcessLoopbackAudioClientAsync(_processId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[process-audio] ActivateAudioInterfaceAsync failed for pid {_processId}: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        if (audioClient is null)
        {
            throw new InvalidOperationException($"ActivateAudioInterfaceAsync returned null IAudioClient for pid {_processId}.");
        }

        // Build a 48 kHz IEEE float stereo WAVEFORMATEX the client will
        // honor. Process loopback doesn't let us query a mix format; we
        // declare the format we want and the OS resamples internally.
        const int blockAlign = DefaultChannels * (BitsPerSampleF32 / 8);
        var waveFormat = new WAVEFORMATEX
        {
            wFormatTag = (ushort)WAVE_FORMAT_IEEE_FLOAT,
            nChannels = (ushort)DefaultChannels,
            nSamplesPerSec = DefaultSampleRate,
            wBitsPerSample = (ushort)BitsPerSampleF32,
            nBlockAlign = (ushort)blockAlign,
            nAvgBytesPerSec = (uint)(DefaultSampleRate * blockAlign),
            cbSize = 0,
        };

        // Marshal the format into native memory for the Initialize call.
        var waveFormatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
        IntPtr eventHandle = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(waveFormat, waveFormatPtr, fDeleteOld: false);

            // LOOPBACK + EVENTCALLBACK. Buffer duration 200 ms (in 100 ns
            // units) — generous enough to absorb any event-handler
            // stall without gapping audio, small enough that the
            // process-audio add-latency doesn't perceptibly misalign
            // with the video track.
            const long BufferDuration100Ns = 200L * 10_000;
            const uint AUDCLNT_SHAREMODE_SHARED = 0;
            const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
            const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;

            var hr = audioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                BufferDuration100Ns,
                0,
                waveFormatPtr,
                IntPtr.Zero);
            if (hr != 0)
            {
                throw new InvalidOperationException($"IAudioClient.Initialize failed (hr=0x{hr:X8}) for pid {_processId}.");
            }

            eventHandle = CreateEventW(IntPtr.Zero, false, false, null);
            if (eventHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateEvent failed for WASAPI event callback.");
            }
            hr = audioClient.SetEventHandle(eventHandle);
            if (hr != 0)
            {
                throw new InvalidOperationException($"IAudioClient.SetEventHandle failed (hr=0x{hr:X8}).");
            }

            // Get the capture client interface.
            var captureIid = IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref captureIid, out var captureClientObj);
            if (hr != 0 || captureClientObj is not IAudioCaptureClient cap)
            {
                throw new InvalidOperationException($"IAudioClient.GetService(IAudioCaptureClient) failed (hr=0x{hr:X8}).");
            }

            _audioClient = audioClient;
            _captureClient = cap;
            _bufferEvent = new ManualResetEventSlim(false);
            _clock = Stopwatch.StartNew();
            _captureCts = new CancellationTokenSource();

            // Wrap eventHandle in a SafeHandle-ish lambda captured by
            // the capture thread. It's torn down in StopAsync; the
            // thread's Close() wake-up comes from SetEvent on dispose.
            var threadEventHandle = eventHandle;
            eventHandle = IntPtr.Zero; // ownership moves to thread

            hr = audioClient.Start();
            if (hr != 0)
            {
                _audioClient = null;
                _captureClient = null;
                throw new InvalidOperationException($"IAudioClient.Start failed (hr=0x{hr:X8}).");
            }

            lock (_lifecycleLock)
            {
                _state = 1;
            }

            _captureThread = new Thread(() => CaptureLoop(threadEventHandle, _captureCts.Token))
            {
                Name = $"process-loopback pid={_processId}",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _captureThread.Start();
            DebugLog.Write($"[process-audio] capture started for pid {_processId} @ {DefaultSampleRate} Hz / {DefaultChannels} ch");
        }
        catch
        {
            if (eventHandle != IntPtr.Zero)
            {
                CloseHandle(eventHandle);
            }
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(waveFormatPtr);
        }
    }

    public Task StopAsync()
    {
        CancellationTokenSource? cts;
        Thread? thread;
        IAudioClient? client;
        ManualResetEventSlim? bufferEvent;
        lock (_lifecycleLock)
        {
            if (_state != 1)
            {
                return Task.CompletedTask;
            }
            _state = 0;
            cts = _captureCts;
            thread = _captureThread;
            client = _audioClient;
            bufferEvent = _bufferEvent;
            _captureCts = null;
            _captureThread = null;
            _audioClient = null;
            _captureClient = null;
            _bufferEvent = null;
        }

        try { cts?.Cancel(); } catch { }
        try { bufferEvent?.Set(); } catch { }
        try { client?.Stop(); } catch { }
        try { thread?.Join(TimeSpan.FromSeconds(2)); } catch { }
        try { bufferEvent?.Dispose(); } catch { }
        if (client is not null)
        {
            try { Marshal.ReleaseComObject(client); } catch { }
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_state == 2)
            {
                return;
            }
        }
        await StopAsync().ConfigureAwait(false);
        lock (_lifecycleLock)
        {
            _state = 2;
        }
    }

    private void CaptureLoop(IntPtr eventHandle, CancellationToken ct)
    {
        var capture = _captureClient;
        if (capture is null)
        {
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait for the OS to signal "a buffer is ready" — the
                // AUDCLNT_STREAMFLAGS_EVENTCALLBACK contract. Timeout at
                // 100 ms so cancellation is responsive even when the
                // target process is silent and no event fires.
                var waitResult = WaitForSingleObject(eventHandle, 100);
                if (ct.IsCancellationRequested || _state != 1)
                {
                    break;
                }
                // WAIT_TIMEOUT (0x102): no audio available this tick —
                // loop and try again. Any other non-OK result means the
                // event got abandoned; bail out.
                if (waitResult != 0 && waitResult != 0x102)
                {
                    DebugLog.Write($"[process-audio] WaitForSingleObject unexpected result=0x{waitResult:X}");
                    break;
                }

                // Drain every packet queued on the client. A single
                // signal may wake us for multiple packets when the
                // source app is busy.
                while (true)
                {
                    var hr = capture.GetNextPacketSize(out var packetFrames);
                    if (hr != 0 || packetFrames == 0)
                    {
                        break;
                    }

                    hr = capture.GetBuffer(out var dataPtr, out var numFrames, out var flags, out _, out _);
                    if (hr != 0)
                    {
                        break;
                    }

                    try
                    {
                        var silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                        var bytesPerFrame = DefaultChannels * (BitsPerSampleF32 / 8);
                        var byteCount = checked((int)numFrames * bytesPerFrame);

                        if (byteCount > 0 && !silent && FrameArrived is not null)
                        {
                            unsafe
                            {
                                var span = new ReadOnlySpan<byte>((void*)dataPtr, byteCount);
                                var ts = _clock?.Elapsed ?? TimeSpan.Zero;
                                var frame = new AudioFrameData(
                                    span,
                                    DefaultSampleRate,
                                    DefaultChannels,
                                    SourceFormat,
                                    ts);
                                try
                                {
                                    FrameArrived(in frame);
                                }
                                catch (Exception ex)
                                {
                                    DebugLog.Write($"[process-audio] subscriber threw: {ex.GetType().Name}: {ex.Message}");
                                }
                            }
                        }
                    }
                    finally
                    {
                        try { capture.ReleaseBuffer(numFrames); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[process-audio] capture loop threw: {ex.GetType().Name}: {ex.Message}");
            try { Closed?.Invoke(); } catch { }
        }
        finally
        {
            if (eventHandle != IntPtr.Zero)
            {
                CloseHandle(eventHandle);
            }
        }
    }

    // ---------------- ActivateAudioInterfaceAsync ----------------

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    /// <summary>
    /// Drives the async COM activation for the virtual process-loopback
    /// device. Returns the raw <see cref="IAudioClient"/> interface; the
    /// caller owns the ref count.
    /// </summary>
    private static Task<IAudioClient?> ActivateProcessLoopbackAudioClientAsync(uint processId)
    {
        var tcs = new TaskCompletionSource<IAudioClient?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Build AUDIOCLIENT_ACTIVATION_PARAMS on the stack-ish heap —
        // we need a stable pointer while the async op runs.
        var paramsStruct = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = processId,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
        };
        var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
        var propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT>());
        try
        {
            Marshal.StructureToPtr(paramsStruct, paramsPtr, fDeleteOld: false);

            var propVariant = new PROPVARIANT
            {
                vt = VT_BLOB,
                // Union field overlay: BLOB at offset 8 on 64-bit
                // (after vt/reserved). We initialize via explicit
                // BlobSize / BlobPtr fields below — layout verified
                // by Marshal.SizeOf == 24 on x64.
                BlobSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                BlobPtr = paramsPtr,
            };
            Marshal.StructureToPtr(propVariant, propVariantPtr, fDeleteOld: false);

            var handler = new ActivationHandler(tcs);
            var iid = IID_IAudioClient;
            var hr = ActivateAudioInterfaceAsync(
                VirtualDevicePath,
                ref iid,
                propVariantPtr,
                handler,
                out var asyncOp);
            if (hr != 0 || asyncOp is null)
            {
                tcs.TrySetException(new InvalidOperationException($"ActivateAudioInterfaceAsync returned hr=0x{hr:X8}"));
            }
            else
            {
                handler.AsyncOp = asyncOp;
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        // Free the native buffers once the activation completes. Hold
        // onto the pointers via the continuation so they outlive the
        // async call.
        return tcs.Task.ContinueWith(t =>
        {
            try { Marshal.FreeHGlobal(paramsPtr); } catch { }
            try { Marshal.FreeHGlobal(propVariantPtr); } catch { }
            if (t.IsFaulted)
            {
                throw t.Exception!.InnerException ?? t.Exception;
            }
            return t.Result;
        }, TaskScheduler.Default);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(
            [MarshalAs(UnmanagedType.Error)] out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr waveFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr waveFormat, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr waveFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    /// <summary>
    /// Managed completion handler — bridges the async COM activation
    /// back into a <see cref="TaskCompletionSource{TResult}"/>. WinRT
    /// calls this on a thread-pool thread when activation finishes.
    /// </summary>
    [ClassInterface(ClassInterfaceType.None)]
    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<IAudioClient?> _tcs;
        public IActivateAudioInterfaceAsyncOperation? AsyncOp;

        public ActivationHandler(TaskCompletionSource<IAudioClient?> tcs)
        {
            _tcs = tcs;
        }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var activateResult, out var activatedObj);
                if (activateResult != 0)
                {
                    _tcs.TrySetException(new InvalidOperationException($"Process-loopback activation failed: hr=0x{activateResult:X8}"));
                    return;
                }
                if (activatedObj is IAudioClient client)
                {
                    _tcs.TrySetResult(client);
                }
                else
                {
                    _tcs.TrySetException(new InvalidOperationException("Activation returned an object that did not implement IAudioClient."));
                }
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }
    }

    // ---------------- Native structs & P/Invoke ----------------

    private const uint AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT = 0;
    private const uint AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    private const uint PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    private const uint PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1;
    private const ushort VT_BLOB = 65;
    private const uint WAVE_FORMAT_IEEE_FLOAT = 0x0003;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public uint ActivationType;
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    // PROPVARIANT is a tagged union — 8 bytes of tag/reserved followed
    // by a 16-byte payload. For VT_BLOB the payload is (uint cbSize, ptr pBlobData).
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public uint BlobSize;
        public uint padding;         // so BlobPtr is 16-byte aligned
        public IntPtr BlobPtr;
        public IntPtr _reserved;
    }

    [DllImport("mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid iid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
