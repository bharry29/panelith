using OmniPanel.Expressions;
using OmniPanel.Sensors;
using OmniPanel.Themes;
using SkiaSharp;

namespace OmniPanel.Rendering.Widgets;

/// <summary>Draws arc gauges with optional centre labels.</summary>
internal static class GaugeWidgetRenderer
{
    public static void Render(GaugeWidget widget, ThemeRect rect, WidgetRenderContext ctx)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        if (!BindingExpression.TryParse(widget.Value, out var expression, out _))
        {
            ctx.DrawErrorMarker(rect);
            return;
        }

        var value = expression!.Evaluate(ctx.Bindings);
        var number = value.AsNumber() ?? 0d;
        var (min, max) = ResolveRange(widget, expression, ctx);
        var span = max - min;
        var t = span <= 0d ? 0d : Math.Clamp((number - min) / span, 0d, 1d);

        var cx = (float)rect.CenterX;
        var cy = (float)rect.CenterY;
        var thickness = (float)Math.Max(1d, widget.Thickness);
        var radius = (float)(Math.Min(rect.Width, rect.Height) / 2d) - (thickness / 2f);
        if (radius <= 0f)
        {
            return;
        }

        var accent = ctx.Colors.Get(widget.Color, ctx.Defaults.AccentColor, new ThemeColor(54, 197, 240));
        var trackColor = ctx.Colors
            .Get(widget.TrackColor, ctx.Defaults.TrackColor, new ThemeColor(31, 39, 51))
            .WithOpacity(ctx.Opacity);
        var fillColor = widget.ColorStops is { Count: > 0 }
            ? ColorStopResolver.ResolveSk(widget.ColorStops, number, accent).WithOpacity(ctx.Opacity)
            : accent.WithOpacity(ctx.Opacity);

        // Skia arcs measure degrees clockwise from 3 o'clock; theme angles are
        // clockwise from 12 o'clock, so subtract 90°.
        var start = (float)(widget.StartAngle - 90d);
        var sweep = (float)widget.SweepAngle;
        var oval = SKRect.Create(cx - radius, cy - radius, radius * 2f, radius * 2f);

        var stroke = ctx.Paints.Stroke;
        stroke.StrokeWidth = thickness;
        stroke.StrokeCap = widget.RoundCaps ? SKStrokeCap.Round : SKStrokeCap.Butt;
        stroke.Style = SKPaintStyle.Stroke;

        stroke.Color = trackColor;
        ctx.Canvas.DrawArc(oval, start, sweep, false, stroke);

        if (t > 0d && Math.Abs(sweep) > 0.01f)
        {
            stroke.Color = fillColor;
            ctx.Canvas.DrawArc(oval, start, sweep * (float)t, false, stroke);
        }

        if (widget.Ticks > 0)
        {
            DrawTicks(widget, cx, cy, radius, thickness, start, sweep, trackColor, ctx);
        }

        if (widget.Label is { } label)
        {
            var local = new ThemeRect(
                rect.X + label.X,
                rect.Y + label.Y,
                label.Width > 0 ? label.Width : rect.Width,
                label.Height > 0 ? label.Height : rect.Height);
            TextWidgetRenderer.Render(label, local, ctx);
        }
    }

    private static void DrawTicks(
        GaugeWidget widget,
        float cx,
        float cy,
        float radius,
        float thickness,
        float start,
        float sweep,
        SKColor color,
        WidgetRenderContext ctx)
    {
        var paint = ctx.Paints.Stroke;
        paint.Color = color;
        paint.StrokeWidth = Math.Max(1f, thickness * 0.25f);
        paint.StrokeCap = SKStrokeCap.Butt;

        var outer = radius + (thickness / 2f);
        var inner = outer - (thickness * 0.6f);
        for (var i = 0; i <= widget.Ticks; i++)
        {
            var angleDeg = start + (sweep * i / widget.Ticks);
            var rad = angleDeg * Math.PI / 180d;
            var cos = (float)Math.Cos(rad);
            var sin = (float)Math.Sin(rad);
            ctx.Canvas.DrawLine(
                cx + (cos * inner),
                cy + (sin * inner),
                cx + (cos * outer),
                cy + (sin * outer),
                paint);
        }
    }

    private static (double Min, double Max) ResolveRange(GaugeWidget widget, BindingExpression expression, WidgetRenderContext ctx)
    {
        if (widget.Minimum is { } min && widget.Maximum is { } max)
        {
            return (min, max);
        }

        var kind = SensorKind.Unknown;
        if (expression.ReferencedSensors.Count > 0)
        {
            kind = ctx.Bindings is ISensorKindProvider provider
                ? provider.KindOf(expression.ReferencedSensors[0])
                : WellKnownSensors.All.GetValueOrDefault(expression.ReferencedSensors[0], SensorKind.Unknown);
        }

        var fallback = SensorKindDefaults.DefaultRangeFor(kind) ?? (0d, 100d);
        return (widget.Minimum ?? fallback.Min, widget.Maximum ?? fallback.Max);
    }
}
