using Panelith.Themes;
using SkiaSharp;

namespace Panelith.Rendering.Widgets;

/// <summary>Draws rectangles, ellipses and lines.</summary>
internal static class ShapeWidgetRenderer
{
    public static void Render(ShapeWidget widget, ThemeRect rect, WidgetRenderContext ctx)
    {
        if (rect.IsEmpty || widget.Opacity <= 0d)
        {
            return;
        }

        var fill = widget.Fill;
        var stroke = widget.Stroke;
        if (fill is null && stroke is null)
        {
            return;
        }

        var skRect = new SKRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);

        if (fill is not null)
        {
            var paint = ctx.Paints.Fill;
            if (widget.GradientTo is not null)
            {
                var from = ctx.Colors.Get(fill, ThemeColor.White).WithOpacity(ctx.Opacity);
                var to = ctx.Colors.Get(widget.GradientTo, ThemeColor.White).WithOpacity(ctx.Opacity);
                var (start, end) = GradientEndpoints(skRect, widget.GradientAngle);
                using var shader = SKShader.CreateLinearGradient(
                    start,
                    end,
                    [from, to],
                    SKShaderTileMode.Clamp);
                paint.Shader = shader;
                paint.Color = SKColors.White;
                DrawPrimitive(widget, skRect, paint, ctx.Canvas);
                paint.Shader = null;
            }
            else
            {
                paint.Color = ctx.ResolveColor(fill, null);
                paint.Shader = null;
                DrawPrimitive(widget, skRect, paint, ctx.Canvas);
            }
        }

        if (stroke is not null && widget.StrokeWidth > 0d)
        {
            var paint = ctx.Paints.Stroke;
            paint.Color = ctx.ResolveColor(stroke, null);
            paint.StrokeWidth = (float)Math.Max(0.5d, widget.StrokeWidth);
            paint.Shader = null;
            DrawPrimitive(widget, skRect, paint, ctx.Canvas);
        }
    }

    private static void DrawPrimitive(ShapeWidget widget, SKRect skRect, SKPaint paint, SKCanvas canvas)
    {
        switch (widget.Shape)
        {
            case ShapeKind.Ellipse:
                canvas.DrawOval(skRect, paint);
                break;

            case ShapeKind.Line:
                canvas.DrawLine(skRect.Left, skRect.Top, skRect.Right, skRect.Bottom, paint);
                break;

            default:
                if (widget.CornerRadius > 0d)
                {
                    canvas.DrawRoundRect(skRect, (float)widget.CornerRadius, (float)widget.CornerRadius, paint);
                }
                else
                {
                    canvas.DrawRect(skRect, paint);
                }

                break;
        }
    }

    private static (SKPoint Start, SKPoint End) GradientEndpoints(SKRect rect, double angleDegrees)
    {
        var angle = angleDegrees * Math.PI / 180d;
        var cx = rect.MidX;
        var cy = rect.MidY;
        var dx = (float)(Math.Cos(angle) * rect.Width / 2d);
        var dy = (float)(Math.Sin(angle) * rect.Height / 2d);
        return (new SKPoint(cx - dx, cy - dy), new SKPoint(cx + dx, cy + dy));
    }
}
