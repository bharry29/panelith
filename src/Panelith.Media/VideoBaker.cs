using System.Runtime.Versioning;
using SkiaSharp;

namespace Panelith.Media;

/// <summary>
/// Bakes a video into a looping <c>.ptclip</c> at the panel's exact size and
/// rate, so playback costs one JPEG decode per frame and no video decoding.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VideoBaker
{
    /// <summary>Bakes <paramref name="request"/>. A cancelled or failed bake leaves no output file.</summary>
    public static ClipInfo Bake(BakeRequest request, IProgress<BakeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        using var decoder = MediaFoundationDecoder.Open(PreparedSource.Resolve(request.Source, cancellationToken));
        var end = request.End ?? decoder.Duration;
        var longest = request.Start + request.MaxLength;
        if (end is null || end > longest)
        {
            end = end is null ? longest : TimeSpan.FromTicks(Math.Min(end.Value.Ticks, longest.Ticks));
        }

        var crop = request.Crop ?? CropRect.Cover(decoder.Width, decoder.Height, request.PanelWidth, request.PanelHeight);
        var output = Path.GetFullPath(request.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var partial = output + ".partial";

        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var writer = new ClipWriter(stream, request.PanelWidth, request.PanelHeight, request.Fps))
            using (var scaler = new FrameScaler(decoder.Width, decoder.Height, crop, request.PanelWidth, request.PanelHeight))
            using (var assembler = new ClipAssembler(writer, scaler, request.Fps, request.Start, end, request.LoopCrossfade, request.Quality))
            {
                decoder.Seek(request.Start);
                var length = (end.Value - request.Start).TotalSeconds;
                while (decoder.Next() is { } frame)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var at = frame.Timestamp;
                    var more = assembler.Add(frame);
                    progress?.Report(new BakeProgress(Math.Clamp((at - request.Start).TotalSeconds / length, 0d, 1d), assembler.Frames));
                    if (!more)
                    {
                        break;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                assembler.Finish();
            }

            var info = ClipFile.ReadInfo(partial);
            File.Move(partial, output, overwrite: true);
            progress?.Report(new BakeProgress(1d, info.FrameCount));
            return info;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Crops and scales a picture into a panel-sized JPEG wallpaper.</summary>
public static class ImageBaker
{
    /// <summary>The largest picture file accepted.</summary>
    public const long MaxSourceBytes = 64L * 1024 * 1024;

    /// <summary>Bakes <paramref name="source"/> to <paramref name="output"/> at the panel size.</summary>
    public static void Bake(string source, string output, int panelWidth, int panelHeight, CropRect? crop = null, int quality = 90)
    {
        ClipLimits.Check(panelWidth, panelHeight, 1, 1);
        var file = new FileInfo(source);
        if (!file.Exists || file.Length > MaxSourceBytes)
        {
            throw new InvalidDataException($"The picture is missing or larger than {MaxSourceBytes / (1024 * 1024)} MB.");
        }

        using var codec = SKCodec.Create(file.FullName) ?? throw new InvalidDataException("The file is not a picture Panelith can read.");
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width is < 1 or > ClipLimits.MaxDimension * 2 || height is < 1 or > ClipLimits.MaxDimension * 2)
        {
            throw new InvalidDataException($"The picture is {width}x{height}, outside the supported size.");
        }

        using var picture = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var result = codec.GetPixels(picture.Info, picture.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidDataException($"The picture could not be decoded ({result}).");
        }

        var area = crop ?? CropRect.Cover(width, height, panelWidth, panelHeight);
        if (!area.IsValid)
        {
            throw new ArgumentException($"The crop {area} is not inside the picture.", nameof(crop));
        }

        using var scaler = new FrameScaler(width, height, area, panelWidth, panelHeight);
        var jpeg = FrameScaler.Encode(scaler.Scale(picture), quality);
        var target = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        AtomicFile.Write(target, jpeg);
    }
}
