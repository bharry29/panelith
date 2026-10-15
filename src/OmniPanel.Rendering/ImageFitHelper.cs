using OmniPanel.Themes;
using SkiaSharp;

namespace OmniPanel.Rendering;

/// <summary>Shared image placement math for backgrounds and image widgets.</summary>
internal static class ImageFitHelper
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    public static void Draw(
        SKCanvas canvas,
        SKBitmap bitmap,
        SKRect dest,
        ImageFit fit,
        SKPaint paint)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || dest.Width <= 0 || dest.Height <= 0)
        {
            return;
        }

        var src = new SKRect(0, 0, bitmap.Width, bitmap.Height);

        switch (fit)
        {
            case ImageFit.Stretch:
                canvas.DrawBitmap(bitmap, dest, Sampling, paint);
                return;

            case ImageFit.None:
            {
                var x = dest.MidX - (bitmap.Width / 2f);
                var y = dest.MidY - (bitmap.Height / 2f);
                canvas.DrawBitmap(bitmap, x, y, Sampling, paint);
                return;
            }

            case ImageFit.Tile:
            {
                using var shader = SKShader.CreateBitmap(bitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
                var saved = paint.Shader;
                var savedColor = paint.Color;
                paint.Shader = shader;
                paint.Color = SKColors.White;
                canvas.DrawRect(dest, paint);
                paint.Shader = saved;
                paint.Color = savedColor;
                return;
            }

            case ImageFit.Cover:
            {
                var scale = Math.Max(dest.Width / bitmap.Width, dest.Height / bitmap.Height);
                var w = bitmap.Width * scale;
                var h = bitmap.Height * scale;
                var dx = dest.MidX - (w / 2f);
                var dy = dest.MidY - (h / 2f);
                var drawRect = SKRect.Create(dx, dy, w, h);
                canvas.Save();
                canvas.ClipRect(dest);
                canvas.DrawBitmap(bitmap, src, drawRect, Sampling, paint);
                canvas.Restore();
                return;
            }

            default: // Contain
            {
                var scale = Math.Min(dest.Width / bitmap.Width, dest.Height / bitmap.Height);
                var w = bitmap.Width * scale;
                var h = bitmap.Height * scale;
                var dx = dest.MidX - (w / 2f);
                var dy = dest.MidY - (h / 2f);
                canvas.DrawBitmap(bitmap, src, SKRect.Create(dx, dy, w, h), Sampling, paint);
                return;
            }
        }
    }
}
