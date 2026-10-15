using System.Runtime.Versioning;

namespace Panelith.Media;

/// <summary>A video's size and length, plus one frame saved as a JPEG for the import screen.</summary>
/// <param name="Width">Decoded width in pixels.</param>
/// <param name="Height">Decoded height in pixels.</param>
/// <param name="Duration">Length, when the file says.</param>
public sealed record VideoProbeResult(int Width, int Height, TimeSpan? Duration);

/// <summary>Reads what the import screen needs to know about a video.</summary>
[SupportedOSPlatform("windows")]
public static class VideoProbe
{
    /// <summary>The longest side of the preview frame.</summary>
    public const int PreviewSize = 960;

    /// <summary>
    /// Opens <paramref name="source"/>, saves the frame at <paramref name="at"/>
    /// (or the last one before the end) to <paramref name="previewJpeg"/>, and
    /// returns the video's size and length.
    /// </summary>
    public static VideoProbeResult Probe(string source, TimeSpan at, string previewJpeg)
    {
        using var decoder = MediaFoundationDecoder.Open(PreparedSource.Resolve(source));
        var scale = Math.Min(1d, PreviewSize / (double)Math.Max(decoder.Width, decoder.Height));
        var width = Math.Max(1, (int)Math.Round(decoder.Width * scale));
        var height = Math.Max(1, (int)Math.Round(decoder.Height * scale));

        decoder.Seek(at);
        IDecodedFrame? chosen = null;
        try
        {
            while (decoder.Next() is { } frame)
            {
                chosen?.Dispose();
                chosen = frame;
                if (frame.Timestamp + frame.Duration > at)
                {
                    break;
                }
            }

            if (chosen is null)
            {
                throw new InvalidDataException("The video has no pictures Panelith can read.");
            }

            using var scaler = new FrameScaler(decoder.Width, decoder.Height, CropRect.Full, width, height);
            var jpeg = FrameScaler.Encode(scaler.Scale(chosen), 85);
            var target = Path.GetFullPath(previewJpeg);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, jpeg);
        }
        finally
        {
            chosen?.Dispose();
        }

        return new VideoProbeResult(decoder.Width, decoder.Height, decoder.Duration);
    }
}
