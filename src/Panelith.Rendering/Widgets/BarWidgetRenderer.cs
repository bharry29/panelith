using Panelith.Expressions;
using Panelith.Sensors;
using Panelith.Themes;
using SkiaSharp;

namespace Panelith.Rendering.Widgets;

/// <summary>Draws linear progress bars, continuous or segmented.</summary>
internal static class BarWidgetRenderer
{
    public static void Render(BarWidget widget, ThemeRect rect, WidgetRenderContext ctx)
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

        var accent = ctx.Colors.Get(widget.Color, ctx.Defaults.AccentColor, new ThemeColor(54, 197, 240));
        var fillColor = widget.ColorStops is { Count: > 0 }
            ? ColorStopResolver.ResolveSk(widget.ColorStops, number, accent).WithOpacity(ctx.Opacity)
            : accent.WithOpacity(ctx.Opacity);
        var trackColor = ctx.Colors
            .Get(widget.TrackColor, ctx.Defaults.TrackColor, new ThemeColor(31, 39, 51))
            .WithOpacity(ctx.Opacity);

        var skRect = new SKRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);
        var radius = (float)Math.Max(0d, widget.CornerRadius);
        var fillPaint = ctx.Paints.Fill;

        if (widget.Segments > 1)
        {
            DrawSegmented(widget, skRect, t, radius, trackColor, fillColor, fillPaint, ctx.Canvas);
            return;
        }

        fillPaint.Color = trackColor;
        DrawBarRect(skRect, radius, fillPaint, ctx.Canvas);

        if (t <= 0d)
        {
            return;
        }

        fillPaint.Color = fillColor;
        var filled = FilledRect(skRect, t, widget.Direction);
        DrawBarRect(filled, radius, fillPaint, ctx.Canvas);
    }

    private static void DrawSegmented(
        BarWidget widget,
        SKRect bounds,
        double t,
        float radius,
        SKColor track,
        SKColor fill,
        SKPaint paint,
        SKCanvas canvas)
    {
        var count = widget.Segments;
        var gap = (float)widget.SegmentGap;
        var horizontal = widget.Direction is BarDirection.LeftToRight or BarDirection.RightToLeft;
        var exact = t * count;

        if (horizontal)
        {
            var totalGap = gap * (count - 1);
            var segW = (bounds.Width - totalGap) / count;
            for (var i = 0; i < count; i++)
            {
                var index = widget.Direction == BarDirection.RightToLeft ? count - 1 - i : i;
                var x = bounds.Left + (index * (segW + gap));
                var seg = SKRect.Create(x, bounds.Top, segW, bounds.Height);
                paint.Color = SegmentColor(i, exact, track, fill);
                DrawBarRect(seg, Math.Min(radius, segW / 2f), paint, canvas);
            }
        }
        else
        {
            var totalGap = gap * (count - 1);
            var segH = (bounds.Height - totalGap) / count;
            for (var i = 0; i < count; i++)
            {
                var index = widget.Direction == BarDirection.TopToBottom ? i : count - 1 - i;
                var y = bounds.Top + (index * (segH + gap));
                var seg = SKRect.Create(bounds.Left, y, bounds.Width, segH);
                // lit count grows from the fill origin.
                var litIndex = widget.Direction == BarDirection.TopToBottom ? i : count - 1 - i;
                paint.Color = SegmentColor(litIndex, exact, track, fill);
                DrawBarRect(seg, Math.Min(radius, segH / 2f), paint, canvas);
            }
        }
    }

    /// <summary>
    /// Segment <paramref name="index"/>'s colour when <paramref name="exact"/>
    /// segments are lit. The segment the value ends in is blended part-way, so
    /// the bar fills smoothly instead of jumping a whole segment at a time.
    /// </summary>
    internal static SKColor SegmentColor(int index, double exact, SKColor track, SKColor fill)
    {
        var amount = Math.Clamp(exact - index, 0d, 1d);
        if (amount >= 1d)
        {
            return fill;
        }

        if (amount <= 0d)
        {
            return track;
        }

        static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + ((b - a) * t));
        return new SKColor(
            Mix(track.Red, fill.Red, amount),
            Mix(track.Green, fill.Green, amount),
            Mix(track.Blue, fill.Blue, amount),
            Mix(track.Alpha, fill.Alpha, amount));
    }

    private static SKRect FilledRect(SKRect bounds, double t, BarDirection direction)
    {
        var amount = (float)t;
        return direction switch
        {
            BarDirection.RightToLeft => SKRect.Create(
                bounds.Right - (bounds.Width * amount),
                bounds.Top,
                bounds.Width * amount,
                bounds.Height),
            BarDirection.BottomToTop => SKRect.Create(
                bounds.Left,
                bounds.Bottom - (bounds.Height * amount),
                bounds.Width,
                bounds.Height * amount),
            BarDirection.TopToBottom => SKRect.Create(
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height * amount),
            _ => SKRect.Create(bounds.Left, bounds.Top, bounds.Width * amount, bounds.Height),
        };
    }

    private static void DrawBarRect(SKRect rect, float radius, SKPaint paint, SKCanvas canvas)
    {
        if (radius > 0f)
        {
            canvas.DrawRoundRect(rect, radius, radius, paint);
        }
        else
        {
            canvas.DrawRect(rect, paint);
        }
    }

    private static (double Min, double Max) ResolveRange(BarWidget widget, BindingExpression expression, WidgetRenderContext ctx)
    {
        if (widget.Minimum is { } min && widget.Maximum is { } max)
        {
            return (min, max);
        }

        var kind = SensorKind.Unknown;
        if (expression.ReferencedSensors.Count > 0 && ctx.Bindings is ISensorKindProvider provider)
        {
            kind = provider.KindOf(expression.ReferencedSensors[0]);
        }
        else if (expression.ReferencedSensors.Count > 0
                 && WellKnownSensors.All.TryGetValue(expression.ReferencedSensors[0], out var known))
        {
            kind = known;
        }

        var fallback = SensorKindDefaults.DefaultRangeFor(kind) ?? (0d, 100d);
        return (widget.Minimum ?? fallback.Min, widget.Maximum ?? fallback.Max);
    }
}
