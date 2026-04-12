using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;
using Vortice.MediaFoundation;
using IVideoEncoder = ScreenSharing.Client.Media.Codecs.IVideoEncoder;

namespace ScreenSharing.Client.Windows.Media.Codecs;

/// <summary>
/// H.264 encoder implemented on top of a Media Foundation <see cref="IMFTransform"/>.
/// Prefers hardware encoder MFTs (NVENC / Intel QSV / AMD VCE) and falls back
/// to software (<c>CLSID_MSH264EncoderMFT</c>) when no hardware encoder is
/// available. Input is NV12 — we convert from the pipeline's I420 buffer
/// inline since NV12 is the most universally supported input format for
/// Windows hardware H.264 encoders.
/// </summary>
internal sealed unsafe class MediaFoundationH264Encoder : IVideoEncoder
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly long _bitrate;
    private readonly IMFTransform _transform;
    private readonly byte[] _nv12Buffer;
    private long _frameIndex;
    private bool _disposed;

    public MediaFoundationH264Encoder(int width, int height, int fps, long bitrate)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _bitrate = Math.Max(500_000, bitrate);
        _nv12Buffer = new byte[width * height * 3 / 2];

        _transform = CreateHardwareEncoder(width, height, _fps, _bitrate)
                     ?? CreateSoftwareEncoder(width, height, _fps, _bitrate)
                     ?? throw new InvalidOperationException("Media Foundation has no usable H.264 encoder on this machine");

        // Tell the MFT we are about to start feeding it samples. Hardware
        // encoders in particular need this before they produce any output.
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
    }

    public VideoCodec Codec => VideoCodec.H264;

    public int Width => _width;

    public int Height => _height;

    public byte[]? EncodeI420(byte[] i420)
    {
        if (_disposed) return null;

        ConvertI420ToNv12(i420, _nv12Buffer, _width, _height);

        // Wrap the NV12 bytes in an IMFSample backed by a single IMFMediaBuffer.
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

        // Sample time / duration in 100-ns units (MF uses 10 MHz ticks).
        var duration = 10_000_000L / _fps;
        sample.SampleTime = _frameIndex * duration;
        sample.SampleDuration = duration;
        _frameIndex++;

        try
        {
            _transform.ProcessInput(0, sample, 0);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[mf] H264 ProcessInput threw: {ex.Message}");
            return null;
        }
        finally
        {
            sample.Dispose();
            buffer.Dispose();
        }

        // Drain all available output samples for this input.
        return DrainOutput();
    }

    private byte[]? DrainOutput()
    {
        List<byte>? accumulator = null;

        while (true)
        {
            var streamInfo = _transform.GetOutputStreamInfo(0);
            var outSample = MediaFactory.MFCreateSample();
            var outBuffer = MediaFactory.MFCreateMemoryBuffer(streamInfo.Size);
            outSample.AddBuffer(outBuffer);

            var db = new OutputDataBuffer
            {
                StreamID = 0,
                Sample = outSample,
            };

            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);

            try
            {
                if ((uint)result.Code == 0xC00D6D72u) // MF_E_TRANSFORM_NEED_MORE_INPUT
                {
                    return accumulator?.ToArray();
                }

                if (result.Failure)
                {
                    DebugLog.Write($"[mf] H264 ProcessOutput failed HRESULT 0x{(uint)result.Code:X8}");
                    return accumulator?.ToArray();
                }

                // Copy encoded bytes out of the MFT's output sample buffer.
                outBuffer.Lock(out nint framePtr, out _, out int curLen);
                try
                {
                    var bytes = new byte[curLen];
                    Marshal.Copy(framePtr, bytes, 0, curLen);
                    (accumulator ??= new List<byte>()).AddRange(bytes);
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0); } catch { }
        try { _transform.Dispose(); } catch { }
    }

    private static IMFTransform? CreateHardwareEncoder(int width, int height, int fps, long bitrate)
    {
        var outputFilter = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };
        var flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
        return TryCreateEncoder(flags, outputFilter, width, height, fps, bitrate, "hardware");
    }

    private static IMFTransform? CreateSoftwareEncoder(int width, int height, int fps, long bitrate)
    {
        var outputFilter = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };
        var flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter);
        return TryCreateEncoder(flags, outputFilter, width, height, fps, bitrate, "software");
    }

    private static IMFTransform? TryCreateEncoder(
        uint flags,
        RegisterTypeInfo outputFilter,
        int width,
        int height,
        int fps,
        long bitrate,
        string label)
    {
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
                    DebugLog.Write($"[mf] {label} H264 SetOutputType threw: {ex.Message}");
                    outputType.Dispose();
                    transform.Dispose();
                    continue;
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
                    DebugLog.Write($"[mf] {label} H264 SetInputType threw: {ex.Message}");
                    inputType.Dispose();
                    transform.Dispose();
                    continue;
                }
                inputType.Dispose();

                DebugLog.Write($"[mf] {label} H264 encoder initialized {width}x{height}@{fps} {bitrate} bps");
                return transform;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[mf] {label} H264 encoder setup threw: {ex.Message}");
                transform?.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// I420 to NV12 conversion. I420 stores Y then U then V as three planes;
    /// NV12 stores Y then a single interleaved UV plane.
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
