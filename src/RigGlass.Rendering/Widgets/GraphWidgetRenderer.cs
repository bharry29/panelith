using RigGlass.Expressions;
using RigGlass.Themes;
using SkiaSharp;

namespace RigGlass.Rendering.Widgets;

/// <summary>Plots sensor history as line / area charts.</summary>
internal static class GraphWidgetRenderer
{
    public static void Render(GraphWidget widget, ThemeRect rect, WidgetRenderContext ctx)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        var skRect = new SKRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);

        if (widget.BackgroundColor is not null)
        {
            var fill = ctx.Paints.Fill;
            fill.Color = ctx.ResolveColor(widget.BackgroundColor, null);
            ctx.Canvas.DrawRect(skRect, fill);
        }

        if (widget.Series.Count == 0)
        {
            return;
        }

        var window = TimeSpan.FromSeconds(Math.Max(0.1d, widget.WindowSeconds));
        var seriesData = new List<(GraphSeries Series, IReadOnlyList<double> Samples)>(widget.Series.Count);
        double yMin = widget.Minimum ?? double.PositiveInfinity;
        double yMax = widget.Maximum ?? double.NegativeInfinity;
        var anySample = false;

        for (var s = 0; s < widget.Series.Count; s++)
        {
            var series = widget.Series[s];
            var reference = ExtractSensorReference(series.Value);
            IReadOnlyList<double> samples = reference is not null
                ? ctx.Bindings.ResolveHistory(reference, window)
                : [];

            if (samples.Count == 0
                && BindingExpression.TryParse(series.Value, out var expr, out _)
                && expr!.Evaluate(ctx.Bindings).AsNumber() is { } current)
            {
                samples = [current];
            }

            seriesData.Add((series, samples));
            if (widget.Minimum is null || widget.Maximum is null)
            {
                for (var i = 0; i < samples.Count; i++)
                {
                    var sample = samples[i];
                    if (double.IsNaN(sample) || double.IsInfinity(sample))
                    {
                        continue;
                    }

                    anySample = true;
                    if (widget.Minimum is null)
                    {
                        yMin = Math.Min(yMin, sample);
                    }

                    if (widget.Maximum is null)
                    {
                        yMax = Math.Max(yMax, sample);
                    }
                }
            }
            else
            {
                anySample |= samples.Count > 0;
            }
        }

        if (!anySample)
        {
            return;
        }

        if (widget.Minimum is { } fixedMin)
        {
            yMin = fixedMin;
        }

        if (widget.Maximum is { } fixedMax)
        {
            yMax = fixedMax;
        }

        if (double.IsInfinity(yMin) || double.IsInfinity(yMax) || yMax <= yMin)
        {
            yMin = 0d;
            yMax = 1d;
        }
        else if (Math.Abs(yMax - yMin) < 1e-9)
        {
            yMin -= 1d;
            yMax += 1d;
        }

        if (widget.GridColor is not null && widget.GridDivisions > 0)
        {
            DrawGrid(widget, skRect, ctx);
        }

        for (var i = 0; i < seriesData.Count; i++)
        {
            var (series, samples) = seriesData[i];
            DrawSeries(series, samples, skRect, yMin, yMax, ctx);
        }
    }

    private static void DrawGrid(GraphWidget widget, SKRect rect, WidgetRenderContext ctx)
    {
        var paint = ctx.Paints.Stroke;
        paint.Color = ctx.ResolveColor(widget.GridColor, null);
        paint.StrokeWidth = 1f;
        for (var i = 0; i <= widget.GridDivisions; i++)
        {
            var y = rect.Top + (rect.Height * i / widget.GridDivisions);
            ctx.Canvas.DrawLine(rect.Left, y, rect.Right, y, paint);
        }
    }

    private static void DrawSeries(
        GraphSeries series,
        IReadOnlyList<double> samples,
        SKRect rect,
        double yMin,
        double yMax,
        WidgetRenderContext ctx)
    {
        if (samples.Count == 0)
        {
            return;
        }

        var points = new SKPoint[samples.Count];
        var usable = 0;
        var ySpan = yMax - yMin;
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (double.IsNaN(sample) || double.IsInfinity(sample))
            {
                continue;
            }

            var x = samples.Count == 1
                ? rect.MidX
                : rect.Left + (rect.Width * i / (samples.Count - 1));
            var yn = (sample - yMin) / ySpan;
            var y = rect.Bottom - (float)(yn * rect.Height);
            points[usable++] = new SKPoint(x, y);
        }

        if (usable == 0)
        {
            return;
        }

        if (usable == 1)
        {
            var paint = ctx.Paints.Fill;
            paint.Color = ctx.ResolveColor(series.Color, ctx.Defaults.AccentColor);
            ctx.Canvas.DrawCircle(points[0], Math.Max(1.5f, (float)series.Thickness), paint);
            return;
        }

        var builder = new SKPathBuilder();
        builder.MoveTo(points[0]);
        if (series.Smooth && usable >= 3)
        {
            for (var i = 1; i < usable; i++)
            {
                var prev = points[i - 1];
                var curr = points[i];
                var midX = (prev.X + curr.X) / 2f;
                builder.QuadTo(prev.X, prev.Y, midX, (prev.Y + curr.Y) / 2f);
                if (i == usable - 1)
                {
                    builder.LineTo(curr);
                }
            }
        }
        else
        {
            for (var i = 1; i < usable; i++)
            {
                builder.LineTo(points[i]);
            }
        }

        using var path = builder.Detach();

        if (series.Fill)
        {
            var fillBuilder = new SKPathBuilder(path);
            fillBuilder.LineTo(points[usable - 1].X, rect.Bottom);
            fillBuilder.LineTo(points[0].X, rect.Bottom);
            fillBuilder.Close();
            using var fillPath = fillBuilder.Detach();
            var fillPaint = ctx.Paints.Fill;
            var baseColor = ctx.Colors.Get(series.Color, ctx.Defaults.AccentColor, new ThemeColor(54, 197, 240));
            fillPaint.Color = baseColor.WithOpacity(ctx.Opacity * Math.Clamp(series.FillOpacity, 0d, 1d));
            ctx.Canvas.DrawPath(fillPath, fillPaint);
        }

        var stroke = ctx.Paints.Stroke;
        stroke.Color = ctx.ResolveColor(series.Color, ctx.Defaults.AccentColor);
        stroke.StrokeWidth = (float)Math.Max(0.5d, series.Thickness);
        stroke.StrokeCap = SKStrokeCap.Round;
        stroke.StrokeJoin = SKStrokeJoin.Round;
        stroke.Style = SKPaintStyle.Stroke;
        ctx.Canvas.DrawPath(path, stroke);
    }

    private static string? ExtractSensorReference(string expression)
    {
        if (!BindingExpression.TryParse(expression, out var parsed, out _))
        {
            return null;
        }

        return parsed!.ReferencedSensors.Count == 1 ? parsed.ReferencedSensors[0] : null;
    }
}
