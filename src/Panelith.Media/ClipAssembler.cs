using SkiaSharp;

namespace Panelith.Media;

/// <summary>
/// Turns decoded source frames, at whatever rate the video has, into clip frames
/// at the panel rate, and optionally blends the end of the loop into its start.
/// </summary>
/// <remarks>
/// <para>
/// Clip frame <c>k</c> shows the latest source frame at or before
/// <c>start + k / fps</c>. A source frame is only scaled when some clip frame
/// needs it, which matters for 60 fps sources baked at 15.
/// </para>
/// <para>
/// The crossfade drops the first half second of the loop and blends the last
/// half second towards it, so the last clip frame flows into the first. It
/// needs the first frames and the latest ones in memory, which is about 30
/// frames of panel size, never the whole clip.
/// </para>
/// </remarks>
public sealed class ClipAssembler : IDisposable
{
    private readonly ClipWriter _writer;
    private readonly FrameScaler _scaler;
    private readonly int _fps;
    private readonly TimeSpan _start;
    private readonly TimeSpan? _end;
    private readonly int _quality;
    private readonly int _fadeFrames;
    private readonly List<SKBitmap> _first = [];
    private readonly Queue<SKBitmap> _recent = new();
    private IDecodedFrame? _held;
    private int _next;
    private bool _done;

    /// <summary>Creates an assembler writing to <paramref name="writer"/>.</summary>
    public ClipAssembler(ClipWriter writer, FrameScaler scaler, int fps, TimeSpan start, TimeSpan? end, bool crossfade, int quality)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(scaler);
        _writer = writer;
        _scaler = scaler;
        _fps = fps;
        _start = start;
        _end = end;
        _quality = quality;
        _fadeFrames = crossfade ? Math.Max(1, (int)Math.Round(BakeRequest.CrossfadeLength.TotalSeconds * fps)) : 0;
    }

    /// <summary>Clip frames produced so far, before the crossfade trims the start.</summary>
    public int Frames => _next;

    /// <summary>Whether the loop is complete, so no more source frames are needed.</summary>
    public bool IsComplete => _done;

    /// <summary>Takes a decoded frame, which the assembler disposes. Returns false once no more are needed.</summary>
    public bool Add(IDecodedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_done)
        {
            frame.Dispose();
            return false;
        }

        if (_held is not null)
        {
            EmitHeld(frame.Timestamp);
            _held.Dispose();
        }

        _held = frame;
        if (_end is { } end && frame.Timestamp >= end)
        {
            EmitHeld(end);
            _done = true;
        }

        return !_done;
    }

    /// <summary>Emits the last frame, applies the crossfade and completes the clip.</summary>
    public void Finish()
    {
        if (_held is not null)
        {
            var shown = _held.Duration > TimeSpan.Zero ? _held.Duration : TimeSpan.FromSeconds(1d / _fps);
            var until = _held.Timestamp + shown;
            if (_end is { } end && end < until)
            {
                until = end;
            }

            EmitHeld(until);
            if (_next == 0)
            {
                Emit(_scaler.Scale(_held), 1);
            }

            _held.Dispose();
            _held = null;
        }

        _done = true;
        if (_next == 0)
        {
            throw new InvalidDataException("The video has no pictures in the chosen range.");
        }

        FlushCrossfade();
        _writer.Complete();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _held?.Dispose();
        _held = null;
        foreach (var bitmap in _first.Concat(_recent))
        {
            bitmap.Dispose();
        }

        _first.Clear();
        _recent.Clear();
    }

    private TimeSpan Target(int index) =>
        _start + TimeSpan.FromTicks((long)Math.Round(index * (double)TimeSpan.TicksPerSecond / _fps));

    private void EmitHeld(TimeSpan until)
    {
        var count = 0;
        while (_next + count < ClipLimits.MaxFrames && Target(_next + count) < until && (_end is not { } end || Target(_next + count) < end))
        {
            count++;
        }

        if (count > 0)
        {
            Emit(_scaler.Scale(_held!), count);
        }

        if (_next >= ClipLimits.MaxFrames)
        {
            _done = true;
        }
    }

    private void Emit(SKBitmap frame, int count)
    {
        _next += count;
        if (_fadeFrames == 0)
        {
            var jpeg = FrameScaler.Encode(frame, _quality);
            for (var i = 0; i < count; i++)
            {
                _writer.Add(jpeg);
            }

            return;
        }

        for (var i = 0; i < count; i++)
        {
            var copy = frame.Copy() ?? throw new InvalidOperationException("Skia failed to copy a clip frame.");
            if (_first.Count < _fadeFrames)
            {
                _first.Add(copy);
                continue;
            }

            _recent.Enqueue(copy);
            if (_recent.Count > _fadeFrames)
            {
                Write(_recent.Dequeue());
            }
        }
    }

    private void FlushCrossfade()
    {
        if (_fadeFrames == 0)
        {
            return;
        }

        // Too short to lose half a second: keep every frame and loop with a cut.
        if (_writer.Count == 0)
        {
            foreach (var bitmap in _first)
            {
                Write(bitmap);
            }

            _first.Clear();
            while (_recent.Count > 0)
            {
                Write(_recent.Dequeue());
            }

            return;
        }

        var index = 0;
        while (_recent.Count > 0)
        {
            using var tail = _recent.Dequeue();
            using var blended = FrameScaler.Blend(tail, _first[index], (index + 1d) / (_fadeFrames + 1d));
            _writer.Add(FrameScaler.Encode(blended, _quality));
            index++;
        }

        foreach (var bitmap in _first)
        {
            bitmap.Dispose();
        }

        _first.Clear();
    }

    private void Write(SKBitmap bitmap)
    {
        using (bitmap)
        {
            _writer.Add(FrameScaler.Encode(bitmap, _quality));
        }
    }
}
