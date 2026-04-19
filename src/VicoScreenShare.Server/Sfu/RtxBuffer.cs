namespace VicoScreenShare.Server.Sfu;

/// <summary>
/// Ring buffer of recent outbound RTP packets indexed by sequence number, so
/// the SFU can service a NACK from a viewer without re-requesting from the
/// upstream publisher. Each subscriber peer keeps its own buffer against the
/// packets it forwarded, so the retransmission matches exactly what the
/// receiver thinks it missed (same SSRC, seq, timestamp, payload).
/// <para>
/// Thread-safety: the SFU forwarding path is single-writer (one
/// <c>SfuSubscriberPeer</c> sequentially processes RTP in its own task), so
/// the buffer doesn't need to lock against itself. If a NACK handler reads
/// on a different thread, wrap calls in a lock at the caller.
/// </para>
/// </summary>
public sealed class RtxBuffer
{
    private readonly int _capacity;
    private readonly Entry[] _slots;
    private readonly Dictionary<ushort, int> _indexBySeq;
    private int _writeIndex;
    private int _count;

    public RtxBuffer(int capacity)
    {
        _capacity = capacity < 0 ? 0 : capacity;
        _slots = new Entry[_capacity];
        _indexBySeq = new Dictionary<ushort, int>(_capacity);
    }

    /// <summary>
    /// Record a packet after it has been forwarded. Evicts the oldest entry
    /// once capacity is exceeded. Re-recording an existing seq overwrites
    /// that slot — useful for the rare case where a NACK handler repacks
    /// a retransmit back into the history.
    /// </summary>
    public void Record(ushort seq, byte[] payload, uint timestamp, int payloadType, bool marker)
    {
        if (_capacity == 0)
        {
            return;
        }

        if (_indexBySeq.TryGetValue(seq, out var existingSlot))
        {
            _slots[existingSlot] = new Entry(seq, payload, timestamp, payloadType, marker);
            return;
        }

        var slot = _writeIndex;
        if (_count == _capacity)
        {
            // Full — evict whatever occupies this slot first.
            var stale = _slots[slot];
            _indexBySeq.Remove(stale.Seq);
        }
        else
        {
            _count++;
        }
        _slots[slot] = new Entry(seq, payload, timestamp, payloadType, marker);
        _indexBySeq[seq] = slot;
        _writeIndex = (slot + 1) % _capacity;
    }

    public bool TryGet(ushort seq, out Entry entry)
    {
        if (_indexBySeq.TryGetValue(seq, out var slot))
        {
            entry = _slots[slot];
            return true;
        }
        entry = default;
        return false;
    }

    public readonly struct Entry
    {
        public Entry(ushort seq, byte[] payload, uint timestamp, int payloadType, bool marker)
        {
            Seq = seq;
            Payload = payload;
            Timestamp = timestamp;
            PayloadType = payloadType;
            Marker = marker;
        }

        public ushort Seq { get; }
        public byte[] Payload { get; }
        public uint Timestamp { get; }
        public int PayloadType { get; }
        public bool Marker { get; }
    }
}
