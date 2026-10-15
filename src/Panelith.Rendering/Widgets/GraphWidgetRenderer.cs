using Panelith.Expressions;
using Panelith.Themes;
using SkiaSharp;

namespace Panelith.Rendering.Widgets;

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
        var seriesData = new List<(GraphSeries Series, List<GraphPoint> Points)>(widget.Series.Count);
        double yMin = widget.Minimum ?? double.PositiveInfinity;
        double yMax = widget.Maximum ?? double.NegativeInfinity;
        var anySample = false;

        for (var s = 0; s < widget.Series.Count; s++)
        {
            var series = widget.Series[s];
            var points = CollectPoints(series, window, ctx);
            seriesData.Add((series, points));
            for (var i = 0; i < points.Count; i++)
            {
                var sample = points[i].Value;
                if (double.IsNaN(sample) || double.IsInfinity(sample) || points[i].X < 0d)
                {
                    continue;
                }

                anySample = true;
                yMin = widget.Minimum ?? Math.Min(yMin, sample);
                yMax = widget.Maximum ?? Math.Max(yMax, sample);
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

        if (widget.Minimum is null || widget.Maximum is null)
        {
            (yMin, yMax) = EaseRange(ctx.StateFor<RangeState>(widget), yMin, yMax, ctx.Ticks);
        }

        if (widget.GridColor is not null && widget.GridDivisions > 0)
        {
            DrawGrid(widget, skRect, ctx);
        }

        ctx.Canvas.Save();
        ctx.Canvas.ClipRect(skRect);
        try
        {
            for (var i = 0; i < seriesData.Count; i++)
            {
                var (series, points) = seriesData[i];
                DrawSeries(series, points, skRect, yMin, yMax, ctx);
            }
        }
        finally
        {
            ctx.Canvas.Restore();
        }
    }

    /// <summary>A plotted sample: X runs from 0 (the window's oldest edge) to 1 (now).</summary>
    internal readonly record struct GraphPoint(double X, double Value);

    /// <summary>
    /// Places samples by their time, so the line scrolls steadily with the
    /// clock rather than jumping one step whenever a reading arrives. The live
    /// (eased) value is drawn at the right edge.
    /// </summary>
    private static List<GraphPoint> CollectPoints(GraphSeries series, TimeSpan window, WidgetRenderContext ctx)
    {
        var points = new List<GraphPoint>();
        var reference = ExtractSensorReference(series.Value);
        if (reference is not null)
        {
            // A little extra history, so the line enters from the left edge.
            var timed = ctx.Bindings.ResolveHistorySamples(reference, window + TimeSpan.FromSeconds(3));
            if (timed.Count > 0)
            {
                var now = ctx.Now > timed[^1].Timestamp ? ctx.Now : timed[^1].Timestamp;
                var live = ctx.Bindings.ResolveSensor(reference);
                var hasLive = live.Kind == BindingValueKind.Number && timed.Count > 1;

                // The live value is still easing toward the newest reading, so
                // drawing both would put a brief spike at the right edge.
                var count = hasLive ? timed.Count - 1 : timed.Count;
                for (var i = 0; i < count; i++)
                {
                    points.Add(new GraphPoint(TimeToX(timed[i].Timestamp, now, window), timed[i].Value));
                }

                if (hasLive)
                {
                    points.Add(new GraphPoint(1d, live.Number));
                }

                return points;
            }

            var samples = ctx.Bindings.ResolveHistory(reference, window);
            for (var i = 0; i < samples.Count; i++)
            {
                points.Add(new GraphPoint(samples.Count == 1 ? 0.5d : (double)i / (samples.Count - 1), samples[i]));
            }
        }

        if (points.Count == 0
            && BindingExpression.TryParse(series.Value, out var expr, out _)
            && expr!.Evaluate(ctx.Bindings).AsNumber() is { } current)
        {
            points.Add(new GraphPoint(0.5d, current));
        }

        return points;
    }

    internal static double TimeToX(DateTimeOffset sample, DateTimeOffset now, TimeSpan window) =>
        1d - ((now - sample).TotalSeconds / window.TotalSeconds);

    /// <summary>How quickly an auto-scaled range shrinks toward a smaller target.</summary>
    private const double RangeSettleSeconds = 0.6d;

    /// <summary>
    /// Grows an auto-scaled range at once, so the line never leaves the box,
    /// but shrinks it gradually, so the whole graph does not lurch when a peak
    /// scrolls out of the window.
    /// </summary>
    internal static (double Min, double Max) EaseRange(RangeState state, double min, double max, long ticks)
    {
        if (!state.Started)
        {
            state.Started = true;
            state.Min = min;
            state.Max = max;
            state.Ticks = ticks;
            return (min, max);
        }

        var seconds = Math.Max(0d, WidgetRenderContext.SecondsBetween(state.Ticks, ticks));
        state.Ticks = ticks;
        var step = 1d - Math.Exp(-seconds / RangeSettleSeconds);
        state.Min = min < state.Min ? min : state.Min + ((min - state.Min) * step);
        state.Max = max > state.Max ? max : state.Max + ((max - state.Max) * step);
        return (state.Min, state.Max);
    }

    internal sealed class RangeState
    {
        public bool Started { get; set; }

        public double Min { get; set; }

        public double Max { get; set; }

        public long Ticks { get; set; }
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
        List<GraphPoint> samples,
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
            var sample = samples[i].Value;
            if (double.IsNaN(sample) || double.IsInfinity(sample))
            {
                continue;
            }

            var x = rect.Left + (float)(rect.Width * samples[i].X);
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
