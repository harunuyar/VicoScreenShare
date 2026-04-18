using FluentAssertions;
using VicoScreenShare.Client.Media;
using VicoScreenShare.Client.Media.Codecs;

namespace VicoScreenShare.Tests.Client;

public class TimestampedFrameQueueTests
{
    private static DecodedVideoFrame MakeFrame(double pts_ms, int width = 2, int height = 2)
    {
        return new DecodedVideoFrame(
            new byte[width * height * 4],
            width,
            height,
            TimeSpan.FromMilliseconds(pts_ms));
    }

    [Fact]
    public void Is_not_ready_until_threshold_reached()
    {
        var queue = new TimestampedFrameQueue(initialPlayoutBufferFrames: 3);
        queue.IsReady.Should().BeFalse();

        queue.Push(MakeFrame(0));
        queue.IsReady.Should().BeFalse("1 < threshold=3");
        queue.Push(MakeFrame(16.67));
        queue.IsReady.Should().BeFalse("2 < threshold=3");
        queue.Push(MakeFrame(33.33));
        queue.IsReady.Should().BeTrue("reached threshold");
    }

    [Fact]
    public void Try_dequeue_returns_frames_in_ascending_timestamp_order()
    {
        var queue = new TimestampedFrameQueue(2);
        queue.Push(MakeFrame(10));
        queue.Push(MakeFrame(20));
        queue.Push(MakeFrame(30));

        queue.TryDequeue(out var a).Should().BeTrue();
        queue.TryDequeue(out var b).Should().BeTrue();
        queue.TryDequeue(out var c).Should().BeTrue();

        a.Timestamp.Should().Be(TimeSpan.FromMilliseconds(10));
        b.Timestamp.Should().Be(TimeSpan.FromMilliseconds(20));
        c.Timestamp.Should().Be(TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public void Out_of_order_push_lands_at_correct_sorted_position()
    {
        var queue = new TimestampedFrameQueue(3);
        queue.Push(MakeFrame(10));
        queue.Push(MakeFrame(30));
        queue.Push(MakeFrame(20));  // should slot between 10 and 30

        queue.TryDequeue(out var a).Should().BeTrue();
        queue.TryDequeue(out var b).Should().BeTrue();
        queue.TryDequeue(out var c).Should().BeTrue();

        a.Timestamp.Should().Be(TimeSpan.FromMilliseconds(10));
        b.Timestamp.Should().Be(TimeSpan.FromMilliseconds(20));
        c.Timestamp.Should().Be(TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public void Peek_next_timestamp_returns_smallest_pending()
    {
        var queue = new TimestampedFrameQueue(2);
        queue.Push(MakeFrame(30));
        queue.Push(MakeFrame(10));
        queue.Push(MakeFrame(20));

        queue.PeekNextTimestamp().Should().Be(TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void Empty_pop_returns_false_and_resets_ready_gate()
    {
        var queue = new TimestampedFrameQueue(2);
        queue.Push(MakeFrame(10));
        queue.Push(MakeFrame(20));
        queue.IsReady.Should().BeTrue();

        queue.TryDequeue(out _).Should().BeTrue();
        queue.TryDequeue(out _).Should().BeTrue();
        queue.TryDequeue(out _).Should().BeFalse("empty after draining");

        queue.IsReady.Should().BeFalse("ready gate resets on empty so the next startup waits for a fresh prebuffer");
    }

    [Fact]
    public void Skip_to_latest_keeps_newest_frames()
    {
        var queue = new TimestampedFrameQueue(3);
        for (var i = 0; i < 20; i++)
        {
            queue.Push(MakeFrame(i * 10));
        }
        queue.Count.Should().Be(20, "no automatic trim on push");

        queue.SkipToLatest(3);
        queue.Count.Should().Be(3);
        // The 3 newest are pts 170, 180, 190.
        queue.PeekNextTimestamp().Should().Be(TimeSpan.FromMilliseconds(170));
    }

    [Fact]
    public void Frame_available_fires_on_push_when_ready()
    {
        var queue = new TimestampedFrameQueue(2);
        var fired = 0;
        queue.FrameAvailable += () => Interlocked.Increment(ref fired);

        queue.Push(MakeFrame(0));
        fired.Should().Be(0, "not ready yet, no signal");

        queue.Push(MakeFrame(10));
        fired.Should().Be(1, "reached threshold → signal on the push that opened the gate");

        queue.Push(MakeFrame(20));
        fired.Should().Be(2, "subsequent pushes continue to wake the loop");
    }

    [Fact]
    public void Clear_empties_and_resets_the_ready_gate()
    {
        var queue = new TimestampedFrameQueue(2);
        queue.Push(MakeFrame(0));
        queue.Push(MakeFrame(10));
        queue.IsReady.Should().BeTrue();

        queue.Clear();
        queue.Count.Should().Be(0);
        queue.IsReady.Should().BeFalse();
    }
}
