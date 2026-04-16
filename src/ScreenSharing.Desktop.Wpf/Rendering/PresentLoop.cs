using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media;
using ScreenSharing.Client.Media.Codecs;

namespace ScreenSharing.Desktop.App.Rendering;

/// <summary>
/// Dedicated paint thread that drains a <see cref="TimestampedFrameQueue"/>
/// and paints frames at their content-timestamp cadence. Scheduling is
/// anchor-based: the first painted frame pins
/// <c>(anchorWall, anchorPts)</c>. Every subsequent paint is scheduled at
/// <c>anchorWall + (frame.Timestamp - anchorPts)</c>. This reproduces
/// the capture source's original frame cadence — the display shows
/// content at the same intervals WGC captured it.
///
/// Paint duration is absorbed into the sleep: if paint takes 5 ms and
/// the content gap is 16 ms, the next paint lands at the full 16 ms
/// offset from the anchor, not 21 ms.
///
/// Re-anchor on catastrophic drift (&gt; 500 ms): source paused,
/// machine slept, long GC — re-anchor at now instead of sprinting
/// through a backlog.
///
/// Thread priority: <see cref="ThreadPriority.AboveNormal"/> so GC
/// pauses and UI thread work don't starve the paint tick.
/// Uses <c>timeBeginPeriod(1)</c> for millisecond-grained sleep.
/// </summary>
public sealed class PresentLoop : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    public delegate void PaintCallback(in DecodedVideoFrame frame);

    private readonly TimestampedFrameQueue _queue;
    private readonly PaintCallback _paint;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);
    private long _loggedFrames;
    private bool _disposed;

    public PresentLoop(TimestampedFrameQueue queue, PaintCallback paint)
    {
        _queue = queue;
        _paint = paint;
        _queue.FrameAvailable += OnFrameAvailable;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PresentLoop",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _wake.Set();
        try { _queue.FrameAvailable -= OnFrameAvailable; } catch { }
        _thread.Join(TimeSpan.FromMilliseconds(500));
        try { _cts.Dispose(); } catch { }
        try { _wake.Dispose(); } catch { }
    }

    private void OnFrameAvailable() => _wake.Set();

    private void Run()
    {
        TimeBeginPeriod(1);
        try
        {
            var ct = _cts.Token;

            var haveAnchor = false;
            long anchorWallTicks = 0;
            TimeSpan anchorPts = default;

            var maxDriftSwTicks = StopwatchTicksFromTimespanTicks(
                TimeSpan.FromMilliseconds(500).Ticks);

            while (!ct.IsCancellationRequested)
            {
                // 1. Dequeue the next frame.
                DecodedVideoFrame current = default;
                while (!ct.IsCancellationRequested)
                {
                    if (_queue.TryDequeue(out current)) break;
                    _wake.WaitOne(16);
                }
                if (ct.IsCancellationRequested) break;

                // 2. Compute scheduled paint time from the content
                //    timestamp, anchored to the first paint's wall time.
                long scheduledWallTicks;
                if (!haveAnchor)
                {
                    anchorWallTicks = Stopwatch.GetTimestamp();
                    anchorPts = current.Timestamp;
                    haveAnchor = true;
                    scheduledWallTicks = anchorWallTicks;
                }
                else
                {
                    var ptsOffset = (current.Timestamp - anchorPts).Ticks;
                    if (ptsOffset < 0) ptsOffset = 0;
                    scheduledWallTicks = anchorWallTicks +
                        StopwatchTicksFromTimespanTicks(ptsOffset);
                }

                // 3. Drift guard. If the schedule is more than 500 ms
                //    away from the wall clock in either direction,
                //    re-anchor to now. Prevents catch-up sprints after
                //    source pauses, machine sleep, or long GC stalls.
                {
                    var nowTicks = Stopwatch.GetTimestamp();
                    if (Math.Abs(nowTicks - scheduledWallTicks) > maxDriftSwTicks)
                    {
                        anchorWallTicks = nowTicks;
                        anchorPts = current.Timestamp;
                        scheduledWallTicks = nowTicks;
                    }
                }

                // 4. Sleep until the scheduled wall time. No-op if
                //    we're already past it.
                SleepUntilStopwatchTicks(scheduledWallTicks, ct);
                if (ct.IsCancellationRequested) break;

                // 5. Paint.
                try
                {
                    _paint(in current);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[present] paint threw: {ex.GetType().Name}: {ex.Message}");
                }

                if (_loggedFrames < 600)
                {
                    _loggedFrames++;
                    var paintedAt = Stopwatch.GetTimestamp();
                    var lateMs = (paintedAt - scheduledWallTicks) * 1000.0 / Stopwatch.Frequency;
                    DebugLog.Write($"[present] painted pts={current.Timestamp.TotalMilliseconds:F2}ms " +
                                   $"queue={_queue.Count} late={lateMs:F2}ms");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[present] loop fatal: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    private static void SleepUntilStopwatchTicks(long targetWall, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var remaining = targetWall - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            var remainingMs = remaining * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2.0)
            {
                Thread.Sleep((int)(remainingMs - 1));
            }
            else
            {
                while (Stopwatch.GetTimestamp() < targetWall && !ct.IsCancellationRequested)
                {
                    Thread.SpinWait(64);
                }
                return;
            }
        }
    }

    private static long StopwatchTicksFromTimespanTicks(long timespanTicks)
    {
        return (long)(timespanTicks * (Stopwatch.Frequency / 10_000_000.0));
    }
}
