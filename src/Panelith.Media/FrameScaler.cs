using SkiaSharp;

namespace Panelith.Media;

/// <summary>A decoded source picture, released once it has been scaled.</summary>
public interface IDecodedFrame : IDisposable
{
    /// <summary>When the picture is shown in the source.</summary>
    TimeSpan Timestamp { get; }

    /// <summary>How long it is shown, or zero when unknown.</summary>
    TimeSpan Duration { get; }

    /// <summary>Copies the picture into an opaque BGRA bitmap of the source size.</summary>
    void CopyTo(SKBitmap destination);
}

/// <summary>
/// Crops source pictures and scales them to the panel, reusing its bitmaps for
/// every frame of a bake.
/// </summary>
public sealed class FrameScaler : IDisposable
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly SKBitmap _source;
    private readonly SKRect _crop;
    private readonly SKCanvas _canvas;
    private readonly SKPaint _paint = new() { IsAntialias = false };

    /// <summary>Creates a scaler for one source size, crop and panel.</summary>
    public FrameScaler(int sourceWidth, int sourceHeight, CropRect crop, int panelWidth, int panelHeight)
    {
        if (sourceWidth is < 1 or > ClipLimits.MaxDimension * 2 || sourceHeight is < 1 or > ClipLimits.MaxDimension * 2)
        {
            throw new InvalidDataException($"A {sourceWidth}x{sourceHeight} source is outside the supported size.");
        }

        _source = new SKBitmap(new SKImageInfo(sourceWidth, sourceHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Output = new SKBitmap(new SKImageInfo(panelWidth, panelHeight, SKColorType.Rgba8888, SKAlphaType.Opaque));
        _canvas = new SKCanvas(Output);
        var (left, top, right, bottom) = crop.ToPixels(sourceWidth, sourceHeight);
        _crop = new SKRect(left, top, right, bottom);
    }

    /// <summary>The last scaled frame. Owned by the scaler and overwritten by <see cref="Scale(IDecodedFrame)"/>.</summary>
    public SKBitmap Output { get; }

    /// <summary>Scales a decoded frame into <see cref="Output"/>.</summary>
    public SKBitmap Scale(IDecodedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        frame.CopyTo(_source);
        _source.NotifyPixelsChanged();
        return Scale(_source);
    }

    /// <summary>Scales a picture of the source size into <see cref="Output"/>.</summary>
    public SKBitmap Scale(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var image = SKImage.FromBitmap(source);
        _canvas.Clear(SKColors.Black);
        _canvas.DrawImage(image, _crop, new SKRect(0, 0, Output.Width, Output.Height), Sampling, _paint);
        _canvas.Flush();
        return Output;
    }

    /// <summary>Encodes a panel-sized bitmap as JPEG.</summary>
    public static byte[] Encode(SKBitmap bitmap, int quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100))
            ?? throw new InvalidOperationException("Skia failed to encode a clip frame.");
        return data.ToArray();
    }

    /// <summary>
    /// Writes <paramref name="from"/> blended towards <paramref name="to"/> by
    /// <paramref name="amount"/> (0 keeps <paramref name="from"/>) into a new bitmap.
    /// </summary>
    public static SKBitmap Blend(SKBitmap from, SKBitmap to, double amount)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var result = from.Copy() ?? throw new InvalidOperationException("Skia failed to copy a clip frame.");
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(amount * 255d), 0, 255)) };
        using var image = SKImage.FromBitmap(to);
        canvas.DrawImage(image, 0, 0, Sampling, paint);
        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _canvas.Dispose();
        _paint.Dispose();
        Output.Dispose();
        _source.Dispose();
    }
}
