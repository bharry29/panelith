using Panelith.Themes;
using SkiaSharp;

namespace Panelith.Rendering.Widgets;

/// <summary>Draws the current frame of a looping clip.</summary>
internal static class VideoWidgetRenderer
{
    public static void Render(VideoWidget widget, ThemeRect rect, WidgetRenderContext ctx)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        var frame = string.IsNullOrWhiteSpace(widget.Source) ? null : ctx.Clips.FrameAt(widget.Source, ctx.Now, widget.Speed);
        if (frame is null)
        {
            ctx.DrawErrorMarker(rect);
            return;
        }

        var paint = ctx.Paints.Image;
        paint.Color = SKColors.White.WithOpacity(ctx.Opacity);
        paint.ColorFilter = null;
        var dest = new SKRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);
        ImageFitHelper.Draw(ctx.Canvas, frame, dest, widget.Fit, paint);
    }
}
