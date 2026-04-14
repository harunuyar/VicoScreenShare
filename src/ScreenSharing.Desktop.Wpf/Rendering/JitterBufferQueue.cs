using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenSharing.Client.Platform;

namespace ScreenSharing.Desktop.App.Rendering;

/// <summary>
/// Receive-side jitter buffer + paint pacer.
///
/// The sender owns the source clock — its pace thread emits frames at
/// exactly its configured frame rate, with monotonic PTSes 1/fps apart.
/// Network noise and decoder timing noise distort the *arrival* cadence
/// at the receiver but not the underlying *send* cadence. This queue
/// holds a small FIFO so the noise has somewhere to go, and a render
/// thread that ticks at exactly the sender's nominal rate. Each tick
/// pops one frame from the head and paints it. The result is a
/// metronome-regular paint cadence at the sender's chosen rate.
///
/// Startup: the render thread blocks until the buffer reaches its
/// configured prebuffer depth before painting the first frame, so a
/// brief network stutter at the very start doesn't immediately
/// underflow the buffer.
///
/// Underflow: if the sender stalls and the buffer empties, the render
/// thread pauses and waits for the buffer to refill back to the
/// prebuffer depth before resuming. This trades a small visible freeze
/// for stable cadence afterwards.
///
/// Overflow: if the source produces faster than the receiver can paint
/// (rare), the FIFO drops the oldest entry on each Submit.
/// </summary>
public sealed class JitterBufferQueue : IDisposable
{
    public delegate void PaintRequestedHandler(in CaptureFrameData frame);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    private sealed class Slot
    {
        public byte[] Buffer = Array.Empty<byte>();
        public int Length;
        public int Width;
        public int Height;
        public int Stride;
        public CaptureFramePixelFormat Format;
        public TimeSpan Pts;
    }

    // Capacity is prebuffer + a bit of headroom so a slow paint or two
    // doesn't immediately drop frames. Total memory is small —
    // capacity × 1080p BGRA ≈ capacity × 8 MB.
    private readonly int _capacity;
    private readonly int _prebufferDepth;
    private readonly Stack<Slot> _freeSlots = new();
    private readonly Queue<Slot> _readySlots = new();
    private readonly object _lock = new();
    private readonly AutoResetEvent _ready = new(false);

    private readonly PaintRequestedHandler _paintCallback;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private volatile int _nominalFrameRate;

    public JitterBufferQueue(PaintRequestedHandler paintCallback, int prebufferDepth = 3)
    {
        _paintCallback = paintCallback;
        _prebufferDepth = Math.Max(1, prebufferDepth);
        _capacity = _prebufferDepth + 2;
        for (var i = 0; i < _capacity; i++) _freeSlots.Push(new Slot());
        _nominalFrameRate = 60;
    }

    /// <summary>
    /// Update the paint clock to a new nominal rate. Called when the
    /// remote streamer's <see cref="ScreenSharing.Protocol.Messages.StreamStarted"/>
    /// arrives with its target fps.
    /// </summary>
    public void SetNominalFrameRate(int fps)
    {
        if (fps <= 0) return;
        _nominalFrameRate = Math.Min(fps, 240);
    }

    /// <summary>Starts the background render thread. Idempotent.</summary>
    public void Start()
    {
        if (_thread is not null || _disposed) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "JitterBufferQueue",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Push a newly arrived frame onto the FIFO.</summary>
    public void Submit(in CaptureFrameData frame)
    {
        if (frame.Format != CaptureFramePixelFormat.Bgra8) return;

        Slot slot;
        lock (_lock)
        {
            if (_disposed) return;

            // Buffer full → drop the oldest. Smoothness is preserved
            // because the next pop's wait equals the gap from the
            // *last painted* frame to the current one (we don't pace
            // off the dropped frame's PTS).
            if (_freeSlots.Count == 0)
            {
                if (_readySlots.Count == 0) return;
                _freeSlots.Push(_readySlots.Dequeue());
            }
            slot = _freeSlots.Pop();

            var len = frame.Pixels.Length;
            if (slot.Buffer.Length < len) slot.Buffer = new byte[len];
            frame.Pixels.CopyTo(slot.Buffer);
            slot.Length = len;
            slot.Width = frame.Width;
            slot.Height = frame.Height;
            slot.Stride = frame.StrideBytes;
            slot.Format = frame.Format;
            slot.Pts = frame.Timestamp;

            _readySlots.Enqueue(slot);
        }
        _ready.Set();
    }

    private void RenderLoop()
    {
        // 1 ms timer slice so Thread.Sleep / spin-wait honor sub-frame
        // intervals.
        TimeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            var ticksPerSecond = (double)Stopwatch.Frequency;
            var ticksPerMs = ticksPerSecond / 1000.0;

            // Diagnostics: log paint cadence once per second.
            long diagLastTicks = sw.ElapsedTicks;
            int diagPaintCount = 0;
            int diagUnderflows = 0;

            // Phase: WaitingForPrebuffer at startup or after underflow.
            // Once the FIFO has _prebufferDepth entries, transition to
            // Playing and tick at the nominal rate.
            bool playing = false;
            long nextDeadline = sw.ElapsedTicks;

            while (_cts is not null && !_cts.IsCancellationRequested)
            {
                if (!playing)
                {
                    // Wait for the FIFO to refill to the prebuffer depth.
                    int depth;
                    lock (_lock) depth = _readySlots.Count;
                    if (depth >= _prebufferDepth || _disposed)
                    {
                        playing = true;
                        nextDeadline = sw.ElapsedTicks;
                    }
                    else
                    {
                        // Wait for a Submit to wake us up. WaitOne
                        // returns when either a Submit fires or the
                        // sentinel cancel happens.
                        _ready.WaitOne();
                        continue;
                    }
                }

                // Sleep + spin-tail to the next paint deadline. Use the
                // currently-published nominal rate so a runtime rate
                // change takes effect on the next tick.
                long intervalTicks = (long)(ticksPerSecond / _nominalFrameRate);
                while (true)
                {
                    long remaining = nextDeadline - sw.ElapsedTicks;
                    if (remaining <= 0) break;
                    var remainingMs = remaining / ticksPerMs;
                    if (remainingMs > 2.0)
                    {
                        Thread.Sleep((int)(remainingMs - 1));
                    }
                    else
                    {
                        while (sw.ElapsedTicks < nextDeadline) Thread.SpinWait(64);
                        break;
                    }
                }

                if (_cts is null || _cts.IsCancellationRequested || _disposed) break;

                // Pop the next frame from the FIFO. If the buffer is
                // empty, transition back to WaitingForPrebuffer — this
                // is the underflow path. Visually the user sees the
                // last frame frozen until the buffer refills, then a
                // resumption at the nominal rate.
                Slot? slot = null;
                lock (_lock)
                {
                    if (_disposed) break;
                    if (_readySlots.Count > 0) slot = _readySlots.Dequeue();
                }
                if (slot is null)
                {
                    playing = false;
                    diagUnderflows++;
                    continue;
                }

                try
                {
                    var data = new CaptureFrameData(
                        slot.Buffer.AsSpan(0, slot.Length),
                        slot.Width, slot.Height, slot.Stride, slot.Format, slot.Pts);
                    _paintCallback(in data);
                    diagPaintCount++;
                }
                catch (Exception ex)
                {
                    ScreenSharing.Client.Diagnostics.DebugLog.Write(
                        $"[jitter] paint callback threw: {ex.Message}");
                }

                lock (_lock) { _freeSlots.Push(slot); }

                nextDeadline += intervalTicks;
                // Reanchor on huge overshoots (paint took way longer
                // than one interval — GC pause, large frame stall).
                long now = sw.ElapsedTicks;
                if (now - nextDeadline > intervalTicks)
                {
                    nextDeadline = now + intervalTicks;
                }

                var sinceLog = sw.ElapsedTicks - diagLastTicks;
                if (sinceLog >= (long)ticksPerSecond)
                {
                    var actualFps = diagPaintCount * ticksPerSecond / sinceLog;
                    ScreenSharing.Client.Diagnostics.DebugLog.Write(
                        $"[jitter] paint={actualFps:F1} fps target={_nominalFrameRate} underflows={diagUnderflows}/s");
                    diagLastTicks = sw.ElapsedTicks;
                    diagPaintCount = 0;
                    diagUnderflows = 0;
                }
            }
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { _ready.Set(); } catch { }
        var t = _thread;
        _thread = null;
        try { t?.Join(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        try { _ready.Dispose(); } catch { }
    }
}
