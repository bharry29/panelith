using OmniPanel.Imaging;
using SkiaSharp;

namespace OmniPanel.Rendering;

/// <summary>
/// SkiaSharp-backed <see cref="IImageCodec"/>.
/// </summary>
/// <remarks>
/// Frame buffers are straight RGBA8888. Every encode/decode path is explicit
/// about <see cref="SKAlphaType.Unpremul"/> so a semi-transparent pixel survives
/// a PNG round-trip without the classic premultiplication darkening.
/// </remarks>
public sealed class SkiaImageCodec : IImageCodec
{
    private readonly object _gate = new();

    /// <inheritdoc />
    public byte[] EncodeJpeg(FrameBuffer frame, int quality = 85)
    {
        ArgumentNullException.ThrowIfNull(frame);
        quality = Math.Clamp(quality, 1, 100);

        lock (_gate)
        {
            using var bitmap = ToBitmap(frame);
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality)
                ?? throw new InvalidOperationException("Skia failed to encode JPEG.");
            return data.ToArray();
        }
    }

    /// <inheritdoc />
    public byte[] EncodePng(FrameBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            using var bitmap = ToBitmap(frame);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Skia failed to encode PNG.");
            return data.ToArray();
        }
    }

    /// <inheritdoc />
    public FrameBuffer Decode(ReadOnlySpan<byte> data, int? width = null, int? height = null)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("Encoded image data is empty.", nameof(data));
        }

        var copy = data.ToArray();

        lock (_gate)
        {
            using var skData = SKData.CreateCopy(copy);
            using var codec = SKCodec.Create(skData)
                ?? throw new InvalidOperationException("Unsupported or corrupt image data.");

            var info = codec.Info;
            var targetW = width ?? info.Width;
            var targetH = height ?? info.Height;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetW);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetH);

            var nativeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var nativeBitmap = new SKBitmap(nativeInfo);
            var result = codec.GetPixels(nativeInfo, nativeBitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new InvalidOperationException($"Skia failed to decode image ({result}).");
            }

            SKBitmap pixels = nativeBitmap;
            SKBitmap? scaled = null;
            try
            {
                if (targetW != info.Width || targetH != info.Height)
                {
                    var desired = new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                    scaled = nativeBitmap.Resize(desired, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
                        ?? throw new InvalidOperationException("Skia failed to resize the decoded image.");
                    pixels = scaled;
                }

                var buffer = FrameBuffer.Rent(targetW, targetH);
                pixels.GetPixelSpan().CopyTo(buffer.Span);
                return buffer;
            }
            finally
            {
                scaled?.Dispose();
            }
        }
    }

    /// <summary>
    /// Binary-searches JPEG quality so the payload fits <paramref name="maxBytes"/>
    /// with fewer encode passes than the interface default step-down loop.
    /// </summary>
    public byte[] EncodeJpegWithinBudget(FrameBuffer frame, int maxBytes, int startQuality = 90)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var high = Math.Clamp(startQuality, 10, 100);
        var low = 10;

        var candidate = EncodeJpeg(frame, high);
        if (candidate.Length <= maxBytes)
        {
            return candidate;
        }

        byte[] best = candidate;
        high--;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            candidate = EncodeJpeg(frame, mid);
            if (candidate.Length <= maxBytes)
            {
                best = candidate;
                low = mid + 1;
            }
            else
            {
                best = candidate;
                high = mid - 1;
            }
        }

        if (best.Length <= maxBytes)
        {
            return best;
        }

        var floor = EncodeJpeg(frame, 10);
        return floor.Length <= best.Length ? floor : best;
    }

    /// <inheritdoc />
    byte[] IImageCodec.EncodeJpegWithinBudget(FrameBuffer frame, int maxBytes, int startQuality) =>
        EncodeJpegWithinBudget(frame, maxBytes, startQuality);

    /// <summary>
    /// Copies the frame into an owned bitmap. Encoding from a pinned
    /// <see cref="SKPixmap"/> and then unpinning before <c>Encode</c> runs is a
    /// use-after-free on SkiaSharp 4 — owned pixels avoid that entirely.
    /// </summary>
    private static SKBitmap ToBitmap(FrameBuffer frame)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        frame.ReadOnlySpan.CopyTo(bitmap.GetPixelSpan());
        return bitmap;
    }
}
