using System;
using System.Collections.Generic;
using System.Threading;
using ScreenSharing.Client.Diagnostics;
using ScreenSharing.Client.Media.Codecs;

namespace ScreenSharing.Client.Media;

/// <summary>
/// Playout queue for decoded frames, ordered strictly by content
/// timestamp. The receiver pushes <see cref="DecodedVideoFrame"/>s in
/// whatever order the decoder delivers them; <see cref="Push"/> places
/// each one at its sorted insertion point (binary search) so frames
/// dequeued are monotonically advancing in content time regardless of
/// decoder reorder behavior or occasional out-of-order network delivery.
///
/// Ready-gating: <see cref="IsReady"/> turns true once
/// <see cref="Count"/> reaches <see cref="InitialPlayoutBufferFrames"/>,
/// and stays true until the queue empties at which point it resets.
/// The present loop waits on this gate before the first paint and after
/// every underflow.
///
/// Overflow policy: at <c>4 * InitialPlayoutBufferFrames</c> entries the
/// oldest frame is dropped (with a rate-limited log) so a stalled
/// present loop can't grow the queue without bound. The factor of 4 is
/// deliberate — it gives homeostasis room to catch up before we start
/// shedding.
///
/// Thread-safety: all state is protected by a single monitor lock.
/// Callers invoke <see cref="Push"/> on whatever thread the decoder
/// produces frames on and <see cref="TryDequeue"/> from the present
/// loop thread; the two never corrupt each other.
/// </summary>
public sealed class TimestampedFrameQueue
{
    private readonly object _lock = new();
    private readonly List<DecodedVideoFrame> _frames;
    private readonly int _initialPlayoutBufferFrames;
    private readonly int _maxCapacity;
    private bool _startupReady;
    private long _droppedOverflowCount;
    private long _lastOverflowLogWallTicks;

    /// <summary>
    /// Raised on the pushing thread whenever a new frame is added. The
    /// present loop uses this to wake up from its park-wait. Deliberately
    /// a bare <see cref="Action"/> — no payload needed, the loop pulls
    /// from the queue itself.
    /// </summary>
    public event Action? FrameAvailable;

    public TimestampedFrameQueue(int initialPlayoutBufferFrames)
    {
        _initialPlayoutBufferFrames = Math.Clamp(initialPlayoutBufferFrames, 1, 240);
        // Bounded capacity: large enough that normal operation never
        // overflows, small enough that a stalled present loop doesn't
        // accumulate minutes of backlog. The floor of 60 ensures that
        // even with InitialPlayoutBufferFrames=1 (minimum-latency mode)
        // the queue can hold a full second of 60 fps content without
        // dropping.
        _maxCapacity = Math.Max(60, _initialPlayoutBufferFrames * 4);
        _frames = new List<DecodedVideoFrame>(_maxCapacity);
    }

    /// <summary>Desired playout buffer depth in frames. Also the target
    /// homeostasis depth the present loop's feedback keeps the queue
    /// near.</summary>
    public int InitialPlayoutBufferFrames => _initialPlayoutBufferFrames;

    /// <summary>Maximum retained frames before the overflow policy kicks
    /// in (oldest dropped).</summary>
    public int MaxCapacity => _maxCapacity;

    /// <summary>Current queue depth. Safe to read from any thread — the
    /// value is a snapshot so treat it as advisory.</summary>
    public int Count
    {
        get { lock (_lock) return _frames.Count; }
    }

    /// <summary>Total frames dropped because the queue overflowed
    /// <see cref="MaxCapacity"/>. Diagnostic only.</summary>
    public long DroppedOverflowCount => Interlocked.Read(ref _droppedOverflowCount);

    /// <summary>
    /// True once the queue has filled to <see cref="InitialPlayoutBufferFrames"/>
    /// at least once, and stays true while frames are available. Resets
    /// to false on underflow — after the queue empties, the next startup
    /// waits for the full initial depth again so the present loop never
    /// paints a single-frame surge right after an underflow.
    /// </summary>
    public bool IsReady
    {
        get { lock (_lock) return _startupReady && _frames.Count > 0; }
    }

    /// <summary>
    /// Insert one decoded frame at its sorted position. Ordering is by
    /// <see cref="DecodedVideoFrame.Timestamp"/> ascending. Duplicates of
    /// the exact same timestamp land side-by-side in FIFO order
    /// (List.BinarySearch returns a bitwise complement of the insertion
    /// point, which for equal keys is past the existing entry).
    /// </summary>
    public void Push(in DecodedVideoFrame frame)
    {
        bool raise;
        lock (_lock)
        {
            // Overflow policy: if we're already at the max, drop the
            // oldest frame so the newer one has somewhere to land.
            if (_frames.Count >= _maxCapacity)
            {
                _frames.RemoveAt(0);
                Interlocked.Increment(ref _droppedOverflowCount);
                RateLimitedOverflowLog();
            }

            // Binary search for the sorted insertion point. The comparer
            // is just Timestamp ascending; we build a sentinel frame
            // with matching Timestamp to call BinarySearch.
            var index = BinarySearchByTimestamp(frame.Timestamp);
            if (index < 0) index = ~index;
            _frames.Insert(index, frame);

            // Startup ready-gate: once we first reach the target depth
            // the gate opens and stays open until underflow.
            if (!_startupReady && _frames.Count >= _initialPlayoutBufferFrames)
            {
                _startupReady = true;
            }

            raise = _startupReady;
        }
        if (raise)
        {
            try { FrameAvailable?.Invoke(); } catch { /* subscriber errors shouldn't break the push */ }
        }
    }

    /// <summary>
    /// Pop the frame with the smallest timestamp. Returns false when the
    /// queue is not ready for playout (either it hasn't reached the
    /// initial buffer depth yet, or it's empty after an underflow and is
    /// re-filling). The caller must react by parking on the next push.
    /// </summary>
    public bool TryDequeue(out DecodedVideoFrame frame)
    {
        lock (_lock)
        {
            if (!_startupReady || _frames.Count == 0)
            {
                // Reset the ready gate on empty so the present loop
                // waits for a fresh prebuffer refill instead of trying
                // to paint each arriving frame immediately.
                if (_frames.Count == 0) _startupReady = false;
                frame = default;
                return false;
            }
            frame = _frames[0];
            _frames.RemoveAt(0);
            if (_frames.Count == 0) _startupReady = false;
            return true;
        }
    }

    /// <summary>
    /// Return the timestamp of the next frame that would be dequeued,
    /// without dequeuing it. The present loop uses this to compute the
    /// "sleep until next.Timestamp - current.Timestamp" interval.
    /// Returns null when the queue is empty.
    /// </summary>
    public TimeSpan? PeekNextTimestamp()
    {
        lock (_lock)
        {
            if (_frames.Count == 0) return null;
            return _frames[0].Timestamp;
        }
    }

    /// <summary>
    /// Discard old frames to reduce the queue depth to approximately
    /// <paramref name="keepCount"/>. Called by the present loop when it
    /// detects it has fallen behind (drift re-anchor). Only the newest
    /// <paramref name="keepCount"/> frames survive; older ones are
    /// silently discarded. The present loop re-anchors its schedule
    /// after calling this so the remaining frames paint from a fresh
    /// baseline instead of chasing a stale clock.
    /// </summary>
    public void SkipToLatest(int keepCount)
    {
        lock (_lock)
        {
            while (_frames.Count > keepCount)
            {
                _frames.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Clear all frames. Called on receiver detach / teardown so a stale
    /// prebuffer doesn't play back to a fresh stream.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _frames.Clear();
            _startupReady = false;
        }
    }

    private int BinarySearchByTimestamp(TimeSpan ts)
    {
        // List.BinarySearch with a custom comparer walks the list with
        // each element's Timestamp. Allocating a sentinel frame here is
        // cheaper than maintaining a parallel List<TimeSpan>.
        int lo = 0;
        int hi = _frames.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int cmp = _frames[mid].Timestamp.CompareTo(ts);
            if (cmp == 0) return mid;
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return ~lo;
    }

    private void RateLimitedOverflowLog()
    {
        // Log at most once per second so an overflow storm doesn't
        // spam the debug log.
        var now = Environment.TickCount64;
        if (now - _lastOverflowLogWallTicks > 1000)
        {
            _lastOverflowLogWallTicks = now;
            DebugLog.Write($"[queue] overflow at cap={_maxCapacity}, dropped oldest (total dropped={_droppedOverflowCount})");
        }
    }
}
