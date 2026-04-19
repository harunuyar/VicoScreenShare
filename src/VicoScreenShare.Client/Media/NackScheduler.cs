namespace VicoScreenShare.Client.Media;

using System;
using System.Collections.Generic;

/// <summary>
/// Tracks inbound RTP sequence numbers and reports gaps as NACK-worthy after
/// a short debounce window. The debounce keeps jitter-reordered packets from
/// triggering NACKs they don't need — if the "missing" packet arrives before
/// the debounce expires the gap is silently cancelled.
/// <para>
/// Caller integrates by calling <see cref="Observe"/> on every received RTP
/// packet (from <c>RTCPeerConnection.OnRtpPacketReceived</c>) and
/// <see cref="PollReady"/> on a timer (every ~10 ms is fine). Seqs returned
/// from PollReady are handed to SIPSorcery's <c>SendRtcpFeedback</c> wrapped
/// in a single RFC 4585 Generic NACK. The scheduler does not track NACK
/// retransmission — if the NACK itself is lost, the receiver's next gap
/// detection (or the publisher's natural keyframe) is the recovery path.
/// </para>
/// </summary>
public sealed class NackScheduler
{
    private readonly TimeSpan _debounce;
    private readonly Func<TimeSpan> _clock;
    private readonly List<PendingSeq> _pending = new();
    private readonly List<ushort> _scratchReady = new();
    private int? _expectedNext;

    public NackScheduler(TimeSpan debounce, Func<TimeSpan> clock)
    {
        _debounce = debounce;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Record an observed inbound RTP sequence number.</summary>
    public void Observe(ushort seq)
    {
        // Late reorder: the seq may already be in pending. Remove it so no
        // NACK fires.
        for (var i = 0; i < _pending.Count; i++)
        {
            if (_pending[i].Seq == seq)
            {
                _pending.RemoveAt(i);
                break;
            }
        }

        if (_expectedNext is null)
        {
            _expectedNext = (ushort)(seq + 1);
            return;
        }

        var expected = (ushort)_expectedNext.Value;
        if (seq == expected)
        {
            _expectedNext = (ushort)(seq + 1);
            return;
        }

        // Forward gap vs late reorder. Compare in signed 16-bit space so the
        // 65535→0 wrap is handled correctly.
        var diff = (short)(seq - expected);
        if (diff > 0)
        {
            var now = _clock();
            for (var i = 0; i < diff; i++)
            {
                var missing = (ushort)(expected + i);
                if (!ContainsSeq(missing))
                {
                    _pending.Add(new PendingSeq(missing, now));
                }
            }
            _expectedNext = (ushort)(seq + 1);
        }
    }

    /// <summary>
    /// Return sequence numbers whose debounce window has elapsed since they
    /// went missing. Insertion-order is preserved so the caller's NACK
    /// packet lists them contiguously across the RTP seq wrap.
    /// </summary>
    public IReadOnlyList<ushort> PollReady()
    {
        _scratchReady.Clear();
        if (_pending.Count == 0)
        {
            return _scratchReady;
        }

        var now = _clock();
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            if (now - _pending[i].SeenAt >= _debounce)
            {
                _scratchReady.Add(_pending[i].Seq);
                _pending.RemoveAt(i);
            }
        }
        // We walked the pending list tail-first to safely remove by index; flip
        // the captured batch back to insertion order for the caller.
        _scratchReady.Reverse();
        return _scratchReady;
    }

    public void Reset()
    {
        _pending.Clear();
        _expectedNext = null;
    }

    private bool ContainsSeq(ushort seq)
    {
        for (var i = 0; i < _pending.Count; i++)
        {
            if (_pending[i].Seq == seq)
            {
                return true;
            }
        }
        return false;
    }

    private readonly record struct PendingSeq(ushort Seq, TimeSpan SeenAt);
}
