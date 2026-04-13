using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using Vortice.MediaFoundation;
using IVideoEncoder = ScreenSharing.Client.Media.Codecs.IVideoEncoder;

namespace ScreenSharing.Client.Windows.Media.Codecs;

/// <summary>
/// H.264 encoder wrapping a Media Foundation <see cref="IMFTransform"/>.
///
/// Modern Windows hardware encoders (NVENC on NVIDIA, QSV on Intel, VCE on
/// AMD) are exposed as ASYNC MFTs: instead of the push-pull sync pattern
/// (ProcessInput, then loop ProcessOutput until need-more-input), they emit
/// <c>METransformNeedInput</c> and <c>METransformHaveOutput</c> events and
/// you drive ProcessInput / ProcessOutput in response to those events.
///
/// This class supports both modes:
///  - Async MFTs run a background event pump that translates the event
///    stream into a <see cref="SemaphoreSlim"/> of "needs input" credits
///    and a <see cref="ConcurrentQueue{T}"/> of encoded output bytes. The
///    caller's <see cref="EncodeI420"/> pushes one frame per call, credits
///    permitting, and drains whatever output has landed — from the
///    caller's perspective it still looks like a synchronous encoder.
///  - Sync MFTs (Microsoft's software H264 encoder and some older drivers)
///    drain output inline after each ProcessInput, same as before.
///
/// Probe order inside the Media Foundation category is:
///   1. hardware async (NVENC / QSV / VCE) — preferred
///   2. hardware sync — older driver variants
///   3. software sync (MSFT MSH264EncoderMFT) — universal fallback
/// </summary>
internal sealed unsafe class MediaFoundationH264Encoder : IVideoEncoder
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly long _bitrate;
    private readonly IMFTransform _transform;
    private readonly bool _isAsync;
    private readonly IMFMediaEventGenerator? _events;
    private readonly Thread? _pumpThread;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly SemaphoreSlim _needInputSignal = new(0, int.MaxValue);
    private readonly ConcurrentQueue<byte[]> _outputQueue = new();
    private readonly object _processLock = new();
    private readonly byte[] _nv12Buffer;
    private long _frameIndex;
    private long _loggedEncodedFrames;
    private bool _disposed;

    public MediaFoundationH264Encoder(int width, int height, int fps, long bitrate)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _bitrate = Math.Max(500_000, bitrate);
        _nv12Buffer = new byte[width * height * 3 / 2];

        (_transform, _isAsync, var pickedLabel) = CreateEncoder(width, height, _fps, _bitrate);
        DebugLog.Write($"[mf] H264 encoder initialized {width}x{height}@{_fps} {_bitrate} bps ({pickedLabel})");

        if (_isAsync)
        {
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            _pumpThread = new Thread(EventPumpLoop)
            {
                IsBackground = true,
                Name = "MF H264 event pump",
            };
            // Start the pump BEFORE we send the streaming messages so no
            // initial NeedInput event races past an unattached queue.
            _pumpThread.Start();
        }

        // NOTIFY_BEGIN_STREAMING allocates native resources (hw context,
        // encoder buffers). Async MFTs additionally require NOTIFY_START_OF_STREAM
        // to actually start emitting METransformNeedInput events — without
        // it, NVENC sits idle and the event pump never gets a credit to
        // release back to EncodeI420.
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        if (_isAsync)
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        }
    }

    public VideoCodec Codec => VideoCodec.H264;

    public int Width => _width;

    public int Height => _height;

    public byte[]? EncodeI420(byte[] i420)
    {
        if (_disposed) return null;

        ConvertI420ToNv12(i420, _nv12Buffer, _width, _height);

        var encoded = _isAsync ? EncodeAsync() : EncodeSync();

        if (encoded is { Length: > 0 } && _loggedEncodedFrames < 3)
        {
            _loggedEncodedFrames++;
            DebugLog.Write($"[mf] H264 encoder produced frame {_loggedEncodedFrames} ({encoded.Length} bytes)");
        }
        return encoded;
    }

    private byte[]? EncodeAsync()
    {
        // Wait briefly for a NeedInput credit. If the encoder is temporarily
        // saturated, skip feeding this frame and just drain any output that
        // the pump already queued — the capture stream keeps flowing at the
        // lower effective rate rather than blocking the capture thread.
        if (!_needInputSignal.Wait(50))
        {
            return TryDequeueOutput();
        }

        var sample = CreateInputSample();
        try
        {
            lock (_processLock)
            {
                if (_disposed) return null;
                try
                {
                    _transform.ProcessInput(0, sample, 0);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mf] async H264 ProcessInput threw: {ex.Message}");
                }
            }
        }
        finally
        {
            sample.Dispose();
        }

        return TryDequeueOutput();
    }

    private byte[]? EncodeSync()
    {
        var sample = CreateInputSample();
        try
        {
            lock (_processLock)
            {
                if (_disposed) return null;
                try
                {
                    _transform.ProcessInput(0, sample, 0);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[mf] sync H264 ProcessInput threw: {ex.Message}");
                    return null;
                }
            }
        }
        finally
        {
            sample.Dispose();
        }

        return DrainOutputLocked();
    }

    private IMFSample CreateInputSample()
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(_nv12Buffer.Length);
        buffer.Lock(out nint ptr, out int maxLen, out _);
        try
        {
            fixed (byte* srcPtr = _nv12Buffer)
            {
                Buffer.MemoryCopy(srcPtr, ptr.ToPointer(), maxLen, _nv12Buffer.Length);
            }
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = _nv12Buffer.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);

        var duration = 10_000_000L / _fps;
        sample.SampleTime = _frameIndex * duration;
        sample.SampleDuration = duration;
        _frameIndex++;

        buffer.Dispose();
        return sample;
    }

    private byte[]? TryDequeueOutput()
    {
        if (_outputQueue.TryDequeue(out var bytes)) return bytes;
        return null;
    }

    /// <summary>
    /// Sync-mode output drain: loop <see cref="IMFTransform.ProcessOutput"/>
    /// until the MFT says it needs more input. The encode lock is held by
    /// the caller.
    /// </summary>
    private byte[]? DrainOutputLocked()
    {
        List<byte>? accumulator = null;

        while (true)
        {
            var output = PullSingleOutput(out var result);
            if (result is DrainResult.NeedMoreInput or DrainResult.Failure)
            {
                return accumulator?.ToArray();
            }
            if (output is null) continue;
            (accumulator ??= new List<byte>()).AddRange(output);
        }
    }

    private enum DrainResult { Success, NeedMoreInput, Failure }

    /// <summary>
    /// Pull one encoded sample from the MFT. Returns the encoded bytes on
    /// success, null on transform stream change (NV12 re-negotiation not
    /// needed for the encoder side), or null with <paramref name="result"/>
    /// flagged when no output is available or an error occurred.
    /// </summary>
    private byte[]? PullSingleOutput(out DrainResult result)
    {
        var streamInfo = _transform.GetOutputStreamInfo(0);
        var bufferSize = streamInfo.Size <= 0 ? (_width * _height) : streamInfo.Size;

        var outSample = MediaFactory.MFCreateSample();
        var outBuffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
        outSample.AddBuffer(outBuffer);

        var db = new OutputDataBuffer
        {
            StreamID = 0,
            Sample = outSample,
        };

        var hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);

        try
        {
            if ((uint)hr.Code == 0xC00D6D72u) // MF_E_TRANSFORM_NEED_MORE_INPUT
            {
                result = DrainResult.NeedMoreInput;
                return null;
            }

            if (hr.Failure)
            {
                DebugLog.Write($"[mf] H264 ProcessOutput failed HRESULT 0x{(uint)hr.Code:X8}");
                result = DrainResult.Failure;
                return null;
            }

            outBuffer.Lock(out nint framePtr, out _, out int curLen);
            try
            {
                var bytes = new byte[curLen];
                Marshal.Copy(framePtr, bytes, 0, curLen);
                result = DrainResult.Success;
                return bytes;
            }
            finally
            {
                outBuffer.Unlock();
            }
        }
        finally
        {
            outSample.Dispose();
            outBuffer.Dispose();
        }
    }

    /// <summary>
    /// Background event pump for async MFTs. Translates NeedInput / HaveOutput
    /// events into semaphore releases and output enqueues. Runs until the
    /// cancellation token is signaled on <see cref="Dispose"/>.
    /// </summary>
    private void EventPumpLoop()
    {
        while (!_pumpCts.IsCancellationRequested)
        {
            IMFMediaEvent? evt;
            try
            {
                evt = _events!.GetEvent(0); // blocking
            }
            catch
            {
                break;
            }
            if (evt is null) continue;

            try
            {
                var type = (MediaEventTypes)evt.EventType;

                if (type == MediaEventTypes.TransformNeedInput)
                {
                    _needInputSignal.Release();
                }
                else if (type == MediaEventTypes.TransformHaveOutput)
                {
                    byte[]? output;
                    lock (_processLock)
                    {
                        if (_disposed) return;
                        output = PullSingleOutput(out _);
                    }
                    if (output is { Length: > 0 })
                    {
                        _outputQueue.Enqueue(output);
                    }
                }
                else if (type == MediaEventTypes.TransformDrainComplete)
                {
                    // End-of-stream handshake — pump can exit after this.
                    return;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] async event pump exception: {ex.Message}");
            }
            finally
            {
                evt.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pumpCts.Cancel(); } catch { }
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0); } catch { }
        // The pump thread exits when GetEvent unblocks or the MFT is disposed;
        // give it a moment but don't hang forever on shutdown.
        _pumpThread?.Join(TimeSpan.FromMilliseconds(500));
        try { _events?.Dispose(); } catch { }
        try { _transform.Dispose(); } catch { }
        try { _pumpCts.Dispose(); } catch { }
        try { _needInputSignal.Dispose(); } catch { }
    }

    // --- Encoder enumeration + type negotiation ---

    private static (IMFTransform transform, bool isAsync, string label) CreateEncoder(int width, int height, int fps, long bitrate)
    {
        // Try async hardware MFTs first — NVENC / QSV / VCE. Passing both
        // HARDWARE and ASYNCMFT flags returns async hardware encoders, which
        // we unlock individually before touching them.
        var async = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagAsyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, tryAsync: true, tryHardware: true, label: "hardware async");
        if (async is not null) return (async, true, "hardware async");

        // Fall back to sync hardware MFTs — rare on modern drivers but they
        // exist (e.g. older Intel QSV builds).
        var sync = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, tryAsync: false, tryHardware: true, label: "hardware sync");
        if (sync is not null) return (sync, false, "hardware sync");

        // Last resort: Microsoft's software H.264 encoder. Always available.
        var software = TryCreateEncoder(
            (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter),
            width, height, fps, bitrate, tryAsync: false, tryHardware: false, label: "software");
        if (software is not null) return (software, false, "software");

        throw new InvalidOperationException("Media Foundation has no usable H.264 encoder on this machine");
    }

    private static IMFTransform? TryCreateEncoder(
        uint flags,
        int width,
        int height,
        int fps,
        long bitrate,
        bool tryAsync,
        bool tryHardware,
        string label)
    {
        var outputFilter = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };

        using var collection = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            flags,
            inputType: null,
            outputType: outputFilter);

        foreach (var activate in collection)
        {
            IMFTransform? transform = null;
            try
            {
                transform = activate.ActivateObject<IMFTransform>();

                // Unlock async MFTs before any type negotiation. Without this
                // SetInputType / SetOutputType fail with MF_E_TRANSFORM_ASYNC_LOCKED.
                if (tryAsync)
                {
                    try
                    {
                        transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write($"[mf] probing {label} H264 MFT — async unlock failed, skipping: {ex.Message}");
                        transform.Dispose();
                        continue;
                    }
                }

                if (!TrySetTypes(transform, width, height, fps, bitrate, label))
                {
                    transform.Dispose();
                    continue;
                }

                return transform;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] probing {label} H264 MFT — activate threw, skipping: {ex.Message}");
                transform?.Dispose();
            }
        }

        return null;
    }

    private static bool TrySetTypes(IMFTransform transform, int width, int height, int fps, long bitrate, string label)
    {
        var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        outputType.SetEnumValue(MediaTypeAttributeKeys.InterlaceMode, VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1u);
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);

        try
        {
            transform.SetOutputType(0, outputType, 0);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] probing {label} H264 MFT — SetOutputType rejected, skipping: {ex.Message}");
            outputType.Dispose();
            return false;
        }
        outputType.Dispose();

        var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        inputType.SetEnumValue(MediaTypeAttributeKeys.InterlaceMode, VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1u);
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);

        try
        {
            transform.SetInputType(0, inputType, 0);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] probing {label} H264 MFT — SetInputType rejected, skipping: {ex.Message}");
            inputType.Dispose();
            return false;
        }
        inputType.Dispose();

        return true;
    }

    // ComCast removed: use transform.QueryInterface<IMFMediaEventGenerator>()
    // directly — SharpGen's base ComObject exposes the right COM QI call so
    // the returned wrapper holds an interface-specific pointer, not the
    // IMFTransform vtable reinterpreted through the wrong slots. Using the
    // wrong vtable was crashing EventPumpLoop with ExecutionEngineException
    // the moment we called GetEvent on it.

    /// <summary>
    /// I420 to NV12 conversion. Y plane copies straight across; U and V
    /// planes are interleaved into a single UV plane.
    /// </summary>
    private static void ConvertI420ToNv12(byte[] i420, byte[] nv12, int width, int height)
    {
        var ySize = width * height;
        var chromaSize = ySize / 4;

        Buffer.BlockCopy(i420, 0, nv12, 0, ySize);

        var uStart = ySize;
        var vStart = ySize + chromaSize;
        var uvStart = ySize;
        for (var i = 0; i < chromaSize; i++)
        {
            nv12[uvStart + i * 2 + 0] = i420[uStart + i];
            nv12[uvStart + i * 2 + 1] = i420[vStart + i];
        }
    }
}
