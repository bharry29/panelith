using System.Diagnostics;
using Panelith.Imaging;

namespace Panelith.Hosting;

/// <summary>
/// Keeps a copy of the most recent frame drawn for a panel so a window can show
/// exactly what the screen is showing.
/// </summary>
/// <remarks>
/// The render loop publishes and the UI pulls on its own timer, so neither waits
/// on the other: a slow window never holds back the panel, and a fast panel
/// never floods the window with frames it cannot paint.
/// </remarks>
public sealed class FrameMirror
{
    private readonly Lock _gate = new();
    private byte[] _pixels = [];
    private int _width;
    private int _height;
    private long _version;
    private long _lastPublished;

    /// <summary>The shortest gap between two published frames.</summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromMilliseconds(66);

    /// <summary>Increments every time a new frame is published.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>When the last frame was published, or null if none has been.</summary>
    public DateTimeOffset? LastPublished { get; private set; }

    /// <summary>The size of the last frame, or (0, 0).</summary>
    public (int Width, int Height) Size
    {
        get
        {
            lock (_gate)
            {
                return (_width, _height);
            }
        }
    }

    /// <summary>Copies a frame in, unless one was published too recently.</summary>
    public void Publish(FrameBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var now = Stopwatch.GetTimestamp();
        if (_lastPublished != 0 && Stopwatch.GetElapsedTime(_lastPublished, now) < MinimumInterval)
        {
            return;
        }

        _lastPublished = now;
        lock (_gate)
        {
            if (_pixels.Length != frame.ByteLength)
            {
                _pixels = new byte[frame.ByteLength];
            }

            frame.ReadOnlySpan.CopyTo(_pixels);
            _width = frame.Width;
            _height = frame.Height;
            LastPublished = DateTimeOffset.Now;
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>
    /// Copies the latest frame, as straight RGBA rows, into a destination whose
    /// rows are <paramref name="destinationStride"/> bytes apart.
    /// </summary>
    /// <returns>False when no frame of that size is available.</returns>
    public bool TryCopyTo(Span<byte> destination, int width, int height, int destinationStride)
    {
        lock (_gate)
        {
            if (_width != width || _height != height || _pixels.Length == 0)
            {
                return false;
            }

            var rowBytes = width * FrameBuffer.BytesPerPixel;
            for (var y = 0; y < height; y++)
            {
                _pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(destination.Slice(y * destinationStride, rowBytes));
            }

            return true;
        }
    }
}
