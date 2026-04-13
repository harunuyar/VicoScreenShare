using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ScreenSharing.Client.Platform;

namespace ScreenSharing.MediaHarness;

/// <summary>
/// <see cref="ICaptureSource"/> backed by a background thread that raises
/// <see cref="FrameArrived"/> with a synthetic BGRA buffer at a fixed
/// frame rate. Used by the network bench to drive a <c>CaptureStreamer</c>
/// without needing to plumb in a real display capture source — the point
/// of that bench is to measure the RTP transport, not the capture path.
/// </summary>
internal sealed class SyntheticFrameSource : ICaptureSource
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly byte[] _bgra;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _disposed;

    public SyntheticFrameSource(int width, int height, int fps)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _bgra = new byte[width * height * 4];

        // Fill with a ramp so the encoder sees some variance instead of
        // a uniform field (which compresses to near-zero bytes).
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = (y * width + x) * 4;
                _bgra[idx + 0] = (byte)(x & 0xFF);
                _bgra[idx + 1] = (byte)(y & 0xFF);
                _bgra[idx + 2] = (byte)((x + y) & 0xFF);
                _bgra[idx + 3] = 0xFF;
            }
        }
    }

    public string DisplayName => "synthetic";

    public event FrameArrivedHandler? FrameArrived;

    public event TextureArrivedHandler? TextureArrived { add { } remove { } }

#pragma warning disable CS0067
    public event Action? Closed;
#pragma warning restore CS0067

    public Task StartAsync()
    {
        if (_thread is not null || _disposed) return Task.CompletedTask;
        _thread = new Thread(RunPumpLoop)
        {
            IsBackground = true,
            Name = "SyntheticFrameSource",
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    private void RunPumpLoop()
    {
        var sw = Stopwatch.StartNew();
        var frame = 0;
        var gapMs = 1000.0 / _fps;
        var nextMs = sw.Elapsed.TotalMilliseconds;
        while (!_cts.IsCancellationRequested)
        {
            // Flip one byte to give the encoder motion between frames —
            // otherwise the rate controller produces almost-empty deltas.
            _bgra[(frame % 1024) * 4] = (byte)(frame & 0xFF);

            var data = new CaptureFrameData(
                _bgra.AsSpan(0, _bgra.Length),
                _width,
                _height,
                strideBytes: _width * 4,
                format: CaptureFramePixelFormat.Bgra8,
                timestamp: TimeSpan.FromMilliseconds(frame * gapMs));
            FrameArrived?.Invoke(in data);
            frame++;

            nextMs += gapMs;
            var now = sw.Elapsed.TotalMilliseconds;
            var waitMs = nextMs - now;
            if (waitMs > 1)
            {
                Thread.Sleep((int)waitMs);
            }
            else if (waitMs < -gapMs * 2)
            {
                nextMs = now;
            }
        }
    }
}
