using System.Runtime.Versioning;

namespace Panelith.Media;

/// <summary>A small JPEG pulled out of a video for the import screen.</summary>
/// <param name="Index">Its place in the filmstrip or the preview.</param>
/// <param name="Time">Where in the video it was taken.</param>
/// <param name="Path">The JPEG file, complete when reported.</param>
public readonly record struct ExtractedFrame(int Index, TimeSpan Time, string Path);

/// <summary>
/// Pulls small pictures out of a video for the import screen: a filmstrip
/// across the whole video, and the frames of the chosen part for playback.
/// Each file is complete before it is reported, so it can be shown at once.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VideoFrames
{
    /// <summary>The longest side of a preview frame.</summary>
    public const int PreviewSize = 960;

    /// <summary>The height of a filmstrip picture.</summary>
    public const int ThumbnailHeight = 90;

    private const int Quality = 80;

    /// <summary>The most video decoded after a seek to reach a filmstrip slot, so far-apart keyframes cannot stall the strip.</summary>
    private static readonly TimeSpan MaxCatchUp = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Writes <paramref name="count"/> pictures spread evenly over the video to
    /// <c>strip-NN.jpg</c> in <paramref name="folder"/>, each the picture
    /// showing in the middle of its slot.
    /// </summary>
    public static int Filmstrip(string source, int count, string folder, Action<ExtractedFrame>? onFrame = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 200);
        using var decoder = MediaFoundationDecoder.Open(PreparedSource.Resolve(source, cancellationToken));
        var duration = decoder.Duration ?? throw new InvalidDataException("The video does not say how long it is.");
        var height = ThumbnailHeight;
        var width = Math.Max(1, (int)Math.Round(decoder.Width * height / (double)decoder.Height));
        using var scaler = new FrameScaler(decoder.Width, decoder.Height, CropRect.Full, width, height);
        Directory.CreateDirectory(folder);
        var written = 0;
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at = TimeSpan.FromTicks((long)(duration.Ticks * (i + 0.5d) / count));
            decoder.Seek(at);
            IDecodedFrame? chosen = null;
            try
            {
                TimeSpan? first = null;
                while (decoder.Next() is { } frame)
                {
                    chosen?.Dispose();
                    chosen = frame;
                    first ??= frame.Timestamp;
                    if (frame.Timestamp + frame.Duration > at || frame.Timestamp - first > MaxCatchUp)
                    {
                        break;
                    }
                }

                if (chosen is null)
                {
                    continue;
                }

                var path = Path.Combine(folder, $"strip-{i:00}.jpg");
                Write(path, FrameScaler.Encode(scaler.Scale(chosen), Quality));
                onFrame?.Invoke(new ExtractedFrame(i, at, path));
                written++;
            }
            finally
            {
                chosen?.Dispose();
            }
        }

        return written;
    }

    /// <summary>
    /// Writes the frames from <paramref name="start"/> to <paramref name="end"/>
    /// at <paramref name="fps"/> to <c>f00000.jpg</c> onwards in
    /// <paramref name="folder"/>, uncropped and at most <see cref="PreviewSize"/>
    /// wide. Frame <c>k</c> is the picture showing at <c>start + k / fps</c>,
    /// as in a bake.
    /// </summary>
    public static int Range(string source, string folder, TimeSpan start, TimeSpan end, int fps, Action<ExtractedFrame>? onFrame = null, CancellationToken cancellationToken = default)
    {
        if (end <= start || start < TimeSpan.Zero)
        {
            throw new ArgumentException("The part must start at or after 0 and end after it starts.");
        }

        ClipLimits.Check(PreviewSize, PreviewSize, fps, 1);
        using var decoder = MediaFoundationDecoder.Open(PreparedSource.Resolve(source, cancellationToken));
        var scale = Math.Min(1d, PreviewSize / (double)Math.Max(decoder.Width, decoder.Height));
        var width = Math.Max(2, (int)Math.Round(decoder.Width * scale));
        var height = Math.Max(2, (int)Math.Round(decoder.Height * scale));
        using var scaler = new FrameScaler(decoder.Width, decoder.Height, CropRect.Full, width, height);
        Directory.CreateDirectory(folder);

        var next = 0;
        TimeSpan Target(int index) => start + TimeSpan.FromTicks((long)Math.Round(index * (double)TimeSpan.TicksPerSecond / fps));

        void EmitUntil(IDecodedFrame held, TimeSpan until)
        {
            byte[]? jpeg = null;
            while (Target(next) < until && Target(next) < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                jpeg ??= FrameScaler.Encode(scaler.Scale(held), Quality);
                var path = Path.Combine(folder, $"f{next:00000}.jpg");
                Write(path, jpeg);
                onFrame?.Invoke(new ExtractedFrame(next, Target(next), path));
                next++;
            }
        }

        decoder.Seek(start);
        IDecodedFrame? held = null;
        try
        {
            while (decoder.Next() is { } frame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (held is not null)
                {
                    EmitUntil(held, frame.Timestamp);
                    held.Dispose();
                }

                held = frame;
                if (frame.Timestamp >= end)
                {
                    break;
                }
            }

            if (held is not null)
            {
                var shown = held.Duration > TimeSpan.Zero ? held.Duration : TimeSpan.FromSeconds(1d / fps);
                EmitUntil(held, held.Timestamp + shown);
            }
        }
        finally
        {
            held?.Dispose();
        }

        if (next == 0)
        {
            throw new InvalidDataException("The video has no pictures in the chosen range.");
        }

        return next;
    }

    private static void Write(string path, byte[] jpeg)
    {
        var partial = path + ".partial";
        File.WriteAllBytes(partial, jpeg);
        File.Move(partial, path, overwrite: true);
    }
}
