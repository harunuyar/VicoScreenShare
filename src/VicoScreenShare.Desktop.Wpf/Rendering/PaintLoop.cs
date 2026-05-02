namespace VicoScreenShare.Desktop.App.Rendering;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using VicoScreenShare.Client.Diagnostics;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;

/// <summary>
/// Dedicated paint thread that drains a <see cref="TimestampedFrameQueue"/>
/// and invokes <see cref="PaintCallback"/> at each frame's
/// content-timestamp cadence. Scheduling is anchor-based: the first
/// painted frame pins <c>(anchorWall, anchorPts)</c> and every
/// subsequent paint runs at <c>anchorWall + (frame.Timestamp -
/// anchorPts)</c>, reproducing the capture source's original frame
/// cadence.
///
/// Paint duration is absorbed into the sleep: if paint takes 5 ms and
/// the content gap is 16 ms, the next paint lands at the full 16 ms
/// offset from the anchor, not 21 ms.
///
/// Re-anchor on catastrophic drift (&gt; 100 ms): source paused,
/// machine slept, long GC — re-anchor at now instead of sprinting
/// through a backlog. <see cref="TimestampedFrameQueue.SkipToLatest"/>
/// drops the stale tail so painting resumes on fresh content.
///
/// Runs at <see cref="ThreadPriority.AboveNormal"/> under
/// <c>timeBeginPeriod(1)</c> so scheduler jitter and GC pauses don't
/// swallow the sub-frame-time sleeps.
/// </summary>
public sealed class PaintLoop : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    public delegate void PaintCallback(in DecodedVideoFrame frame);

    private readonly TimestampedFrameQueue _queue;
    private readonly PaintCallback _paint;
    private MediaClock? _mediaClock;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly AutoResetEvent _wake = new(false);
    private long _loggedFrames;
    private bool _disposed;

    /// <summary>
    /// Worst <c>iterMs</c> observed since the last call to
    /// <see cref="ReadAndResetWorstIterMs"/>. <c>iterMs</c> is the
    /// time from one successful paint's end to the next — captures
    /// dequeue-wait + paint cost. A 1-second user-visible freeze
    /// shows up as iterMs ~1000 inside one painter iteration even
    /// when the 2-second <c>paint-pulse</c> averages out healthy.
    /// Read by the renderer's pulse logger; reset on read so each
    /// pulse window reports its own worst case.
    /// </summary>
    private double _worstIterMsSincePulse;
    private readonly object _worstIterLock = new();

    public double ReadAndResetWorstIterMs()
    {
        lock (_worstIterLock)
        {
            var v = _worstIterMsSincePulse;
            _worstIterMsSincePulse = 0;
            return v;
        }
    }

    public PaintLoop(TimestampedFrameQueue queue, PaintCallback paint)
    {
        _queue = queue;
        _paint = paint;
        _queue.FrameAvailable += OnFrameAvailable;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PaintLoop",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    /// <summary>
    /// Attach a shared <see cref="MediaClock"/> for A/V sync. The
    /// loop latches the clock's anchor at the moment of every
    /// (re-)anchor — first paint and any drift-induced re-anchor —
    /// so audio playout always knows the wall time of the video
    /// stream's *current* schedule, no matter what the playout
    /// buffer depth is or how long the publisher's pacer / network
    /// jitter delays a frame.
    ///
    /// Settable rather than ctor-bound because the renderer creates
    /// PaintLoop before its <c>Receiver</c> dependency property is
    /// bound; the receiver carries the per-session MediaClock and
    /// we attach it once the receiver is known.
    /// </summary>
    public void SetMediaClock(MediaClock? mediaClock)
    {
        _mediaClock = mediaClock;
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

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
            // Tracks whether we've successfully pushed an anchor into
            // the shared MediaClock yet. Decoupled from haveAnchor
            // because PaintLoop's anchor is set on first paint, but
            // the MediaClock anchor requires the video Sender Report
            // to be present so it can translate the per-stream
            // content time into the publisher NTP domain. SR can
            // arrive seconds after first paint — we retry every
            // paint until it lands.
            var avSyncAnchored = false;

            // Drift threshold for the catch-up guard. 100 ms is ~6 frames
            // at 60 fps — enough headroom for normal PTS jitter but
            // tight enough that a visible backlog (cold-start, stall)
            // gets caught quickly.
            var maxDriftSwTicks = StopwatchTicksFromTimespanTicks(
                TimeSpan.FromMilliseconds(100).Ticks);

            // Stutter detection: log whenever the dequeue wait — the gap
            // between successive successful paints — exceeds a threshold.
            // Captures sub-pulse freezes (40–500 ms) that the 2 s aggregate
            // pulses hide. Threshold is set above normal jitter and below
            // the user-perceptible "frozen for a beat" feeling.
            const double StutterLogThresholdMs = 15.0;
            long lastDequeueTicks = 0;
            // Queue-exhaustion probe: count how many WaitOne iterations
            // we cycle through before dequeue succeeds. > 0 means the
            // playout queue went empty momentarily (PaintLoop drained
            // ahead of decode/transport). The value is the number of
            // 16 ms wakeups before a frame appeared, so values >= 2
            // mean the queue was empty for ~32 ms or more.

            while (!ct.IsCancellationRequested)
            {
                // 1. Dequeue the next frame.
                DecodedVideoFrame current = default;
                var waitStartTicks = Stopwatch.GetTimestamp();
                var waitIterations = 0;
                while (!ct.IsCancellationRequested)
                {
                    if (_queue.TryDequeue(out current))
                    {
                        break;
                    }

                    _wake.WaitOne(16);
                    waitIterations++;
                }
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var dequeuedAtTicks = Stopwatch.GetTimestamp();
                // If we burnt > 1 wait cycle (= ~32 ms with empty queue)
                // before a frame appeared, log it directly. This catches
                // queue-exhaustion that the steady-state paint cadence
                // would otherwise hide — paint catches up, but the user
                // saw a frozen tile for that duration.
                if (waitIterations >= 2)
                {
                    DebugLog.Write(
                        $"[paint-empty] queue exhausted {waitIterations} cycles (~{waitIterations * 16}ms) qDepth={_queue.Count} pts={current.Timestamp.TotalMilliseconds:F0}ms");
                }

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
                    if (ptsOffset < 0)
                    {
                        ptsOffset = 0;
                    }

                    scheduledWallTicks = anchorWallTicks +
                        StopwatchTicksFromTimespanTicks(ptsOffset);
                }

                // Push the current PaintLoop anchor into the shared
                // MediaClock. Retried every paint until SR arrives —
                // typically lands seconds AFTER first paint, well
                // after PaintLoop has its own anchor. Translate the
                // CURRENT frame's content time into the wall time
                // PaintLoop is about to paint at, so audio sees the
                // SAME (wall, contentTime) pair PaintLoop uses for
                // video scheduling.
                if (!avSyncAnchored && _mediaClock is not null)
                {
                    avSyncAnchored = TryPublishAnchor(current.Timestamp, scheduledWallTicks);
                }

                // 3. Drift guard + latency catch-up. If the schedule
                //    drifted behind the wall clock by more than a small
                //    threshold, skip ahead in the queue to near-latest
                //    content, then re-anchor. This handles cold-start
                //    backlog (encoder init builds up 10+ frames before
                //    the first paint) and mid-stream stalls (D3D
                //    contention, GC pause) without the aggressive
                //    depth-based trimming that drops frames during
                //    normal operation. The threshold is generous enough
                //    (~100 ms) that normal PTS jitter doesn't trigger
                //    it, but tight enough that a visible stall gets
                //    caught within a few frames.
                {
                    var nowTicks = Stopwatch.GetTimestamp();
                    var driftSwTicks = nowTicks - scheduledWallTicks;
                    var driftDetected =
                        driftSwTicks > maxDriftSwTicks || driftSwTicks < -maxDriftSwTicks;

                    // Queue-depth-based catch-up. Once the painter falls
                    // behind, the queue's overflow policy (drop oldest)
                    // collapses the per-frame pts gap from 7 ms (144 fps
                    // production) to ~33 ms (30 fps consumption), so the
                    // *measured* drift between scheduledWallTicks and now
                    // converges to ~0 even though we're persistently 4×
                    // behind real time. The drift guard above never fires
                    // again, and the 30 fps cadence locks in for the rest
                    // of the session. Detect this state by queue depth: if
                    // the queue stays above half capacity, the producer is
                    // outrunning us. Trip the same skip-to-latest path so
                    // the painter realigns to fresh frames at the real
                    // production cadence. Threshold leaves comfortable
                    // headroom for legitimate IDR-burst absorption (which
                    // pushes the queue into the 20-30 range transiently).
                    var depthCatchupThreshold = _queue.MaxCapacity / 2;
                    var depthCatchup = !driftDetected
                        && _queue.Count >= depthCatchupThreshold;

                    if (driftDetected || depthCatchup)
                    {
                        if (depthCatchup)
                        {
                            DebugLog.Write($"[paint-catchup] queue depth {_queue.Count}/{_queue.MaxCapacity} ≥ {depthCatchupThreshold}; skip-to-latest + re-anchor");
                        }
                        // Skip the queue to near-latest so we resume
                        // painting fresh content, not a stale backlog.
                        _queue.SkipToLatest(_queue.InitialPlayoutBufferFrames);
                        // Re-dequeue: the frame we're holding might have
                        // been trimmed or is now stale. Get the freshest.
                        if (_queue.TryDequeue(out var fresher))
                        {
                            current = fresher;
                        }
                        anchorWallTicks = nowTicks;
                        anchorPts = current.Timestamp;
                        scheduledWallTicks = nowTicks;
                        // Re-anchor the shared MediaClock too. Audio's
                        // playout follows wherever video re-anchors so
                        // a paused/resumed source or a long GC keeps
                        // the streams aligned. Idempotent if SR isn't
                        // there yet — the per-paint retry above will
                        // pick it up once it arrives.
                        if (_mediaClock is not null)
                        {
                            avSyncAnchored = TryPublishAnchor(current.Timestamp, anchorWallTicks)
                                || avSyncAnchored;
                        }
                    }
                }

                // 4. Sleep until the scheduled wall time. No-op if
                //    we're already past it.
                var preSleepTicks = Stopwatch.GetTimestamp();
                SleepUntilStopwatchTicks(scheduledWallTicks, ct);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // 5. Paint.
                var prePaintTicks = Stopwatch.GetTimestamp();
                try
                {
                    _paint(in current);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[paint] paint threw: {ex.GetType().Name}: {ex.Message}");
                }

                // Diagnostic: split the total iteration time into its parts
                // so a 50 ms cadence shows up as e.g. "sleep=43 paint=7" or
                // "sleep=0 paint=49" — telling us where the time goes.
                if (lastDequeueTicks != 0)
                {
                    var paintEndTicks = Stopwatch.GetTimestamp();
                    var iterMs = (paintEndTicks - lastDequeueTicks) * 1000.0 / Stopwatch.Frequency;
                    lock (_worstIterLock)
                    {
                        if (iterMs > _worstIterMsSincePulse)
                        {
                            _worstIterMsSincePulse = iterMs;
                        }
                    }
                    if (iterMs > StutterLogThresholdMs)
                    {
                        var dequeueWaitMs = (dequeuedAtTicks - waitStartTicks) * 1000.0 / Stopwatch.Frequency;
                        var sleepMs = (prePaintTicks - preSleepTicks) * 1000.0 / Stopwatch.Frequency;
                        var paintMs = (paintEndTicks - prePaintTicks) * 1000.0 / Stopwatch.Frequency;
                        var preWorkMs = (preSleepTicks - dequeuedAtTicks) * 1000.0 / Stopwatch.Frequency;
                        DebugLog.Write(
                            $"[paint-stutter] iter={iterMs:F1}ms wait={dequeueWaitMs:F1}ms " +
                            $"prework={preWorkMs:F1}ms sleep={sleepMs:F1}ms paint={paintMs:F1}ms " +
                            $"waitIter={waitIterations} qDepth={_queue.Count} pts={current.Timestamp.TotalMilliseconds:F0}ms");
                    }
                }
                lastDequeueTicks = Stopwatch.GetTimestamp();

                if (_loggedFrames < 20)
                {
                    _loggedFrames++;
                    var paintedAt = Stopwatch.GetTimestamp();
                    var lateMs = (paintedAt - scheduledWallTicks) * 1000.0 / Stopwatch.Frequency;
                    DebugLog.Write($"[paint] painted pts={current.Timestamp.TotalMilliseconds:F2}ms " +
                                   $"queue={_queue.Count} late={lateMs:F2}ms");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[paint] loop fatal: {ex.GetType().Name}: {ex.Message}");
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
            if (remaining <= 0)
            {
                return;
            }

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

    /// <summary>
    /// Push the freshly-set <c>(anchorPts, anchorWall)</c> pair into
    /// the shared <see cref="MediaClock"/>. Returns <c>true</c> when
    /// the clock latched, <c>false</c> when the video Sender Report
    /// hasn't arrived yet — caller retries on the next paint until
    /// the SR lands. <see cref="MediaClock"/> translates the
    /// per-stream content time through the video SR to publisher NTP
    /// and uses that as its anchor; audio queries the same clock and
    /// lands on the same wall-clock line. Returns false when the
    /// session has no MediaClock (self-preview, harness) so caller
    /// stops retrying.
    /// </summary>
    private bool TryPublishAnchor(TimeSpan contentTime, long localStopwatchTicks)
    {
        if (_mediaClock is null)
        {
            return false;
        }
        try
        {
            return _mediaClock.SetVideoAnchorFromContentTime(contentTime, localStopwatchTicks);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[paint] TryPublishAnchor threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
