namespace VicoScreenShare.Desktop.App.Rendering;

using System;
using System.Threading;
using System.Windows.Media;

/// <summary>
/// Measures the process-wide <see cref="CompositionTarget.Rendering"/>
/// tick rate — the true rate at which WPF is compositing visuals,
/// independent of how fast individual renderers post
/// <c>AddDirtyRect</c>. Used to distinguish "our renderer is
/// submitting at 144 Hz" (the AddDirtyRect count) from "WPF is
/// composing at 144 Hz" (what the user actually sees).
///
/// <c>CompositionTarget.Rendering</c> is a static per-dispatcher event
/// that fires once per compose tick regardless of subscriber count.
/// Multiple renderer instances share a single underlying subscription
/// via the internal refcount so adding / removing handlers doesn't
/// inflate the counter.
/// </summary>
internal static class WpfCompositionMetrics
{
    private static int s_subscriberCount;
    private static long s_tickCount;
    private static readonly object s_lock = new();

    public static long TickCount => Interlocked.Read(ref s_tickCount);

    public static void Subscribe()
    {
        lock (s_lock)
        {
            s_subscriberCount++;
            if (s_subscriberCount == 1)
            {
                CompositionTarget.Rendering += OnRendering;
            }
        }
    }

    public static void Unsubscribe()
    {
        lock (s_lock)
        {
            if (s_subscriberCount == 0)
            {
                return;
            }
            s_subscriberCount--;
            if (s_subscriberCount == 0)
            {
                CompositionTarget.Rendering -= OnRendering;
            }
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
        => Interlocked.Increment(ref s_tickCount);
}
