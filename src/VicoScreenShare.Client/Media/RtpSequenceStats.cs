namespace VicoScreenShare.Client.Media;

/// <summary>
/// RFC 3550 §A.1 / §A.3 RTP sequence accounting: tracks first and highest
/// sequence numbers seen for a given SSRC, plus the 16-bit rollover count,
/// and derives packet-loss counts from <c>expected - received</c>.
///
/// Why the derivation rather than a running "lost" counter: a forward gap
/// only means "packets we haven't seen yet in this window," and some of
/// those may arrive later (reorder). By recomputing from
/// <c>(cycles &lt;&lt; 16) + maxSeq - baseSeq + 1 - received</c>, a late
/// reorder arrival decrements the reported loss back toward truth. The
/// previous design added to a running lost counter on every forward jump
/// and never walked it back, so every network reorder permanently bumped
/// the reported loss.
///
/// All mutation + derived reads are serialized on an internal lock. Safe
/// to call <see cref="Observe"/> from the SIPSorcery receive thread while
/// the UI stats timer reads <see cref="Received"/> / <see cref="Lost"/> /
/// <see cref="LossPercent"/> concurrently.
/// </summary>
public sealed class RtpSequenceStats
{
    private readonly object _gate = new();
    private uint _ssrc;
    private ushort _baseSeq;
    private ushort _maxSeq;
    private uint _cycles;
    private long _received;
    private bool _initialized;

    /// <summary>
    /// Feed one received packet's SSRC + sequence number. SSRC changes
    /// reset the tracker (treat as a new stream — publisher restart or
    /// SFU rekey).
    /// </summary>
    public void Observe(uint ssrc, ushort seq)
    {
        lock (_gate)
        {
            if (_ssrc != ssrc)
            {
                _ssrc = ssrc;
                _initialized = false;
                _cycles = 0;
                _received = 0;
            }

            if (!_initialized)
            {
                _baseSeq = seq;
                _maxSeq = seq;
                _initialized = true;
                _received = 1;
                return;
            }

            // RFC 3550 §A.1: the delta between incoming seq and stored max,
            // treated as a 16-bit unsigned quantity, tells us whether this
            // packet is "after" max (0..0x7FFF) or a reorder (0x8000..0xFFFF).
            // A forward arrival that wraps (seq < maxSeq but still "forward")
            // bumps the cycle counter; reorders leave max untouched so the
            // derived loss naturally decreases when the straggler arrives.
            var diff = (seq - _maxSeq) & 0xFFFF;
            if (diff < 0x8000)
            {
                if (seq < _maxSeq)
                {
                    _cycles++;
                }
                _maxSeq = seq;
            }

            _received++;
        }
    }

    /// <summary>Total RTP packets received (including reorders).</summary>
    public long Received
    {
        get { lock (_gate) { return _received; } }
    }

    /// <summary>
    /// Derived packet loss: <c>expected - received</c>. Drops when a late
    /// reorder arrival catches up. Never negative (clamped).
    /// </summary>
    public long Lost
    {
        get
        {
            lock (_gate)
            {
                if (!_initialized)
                {
                    return 0;
                }
                var expected = ((long)_cycles << 16) + _maxSeq - _baseSeq + 1;
                var lost = expected - _received;
                return lost > 0 ? lost : 0;
            }
        }
    }

    /// <summary>
    /// Loss as a percentage of expected, <c>0..100</c>. Returns 0 when no
    /// packets have been observed yet.
    /// </summary>
    public double LossPercent
    {
        get
        {
            lock (_gate)
            {
                if (!_initialized)
                {
                    return 0.0;
                }
                var expected = ((long)_cycles << 16) + _maxSeq - _baseSeq + 1;
                if (expected <= 0)
                {
                    return 0.0;
                }
                var lost = expected - _received;
                if (lost <= 0)
                {
                    return 0.0;
                }
                return 100.0 * lost / expected;
            }
        }
    }
}
