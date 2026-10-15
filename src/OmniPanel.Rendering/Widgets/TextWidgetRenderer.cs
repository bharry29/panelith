using System.Text;
using OmniPanel.Expressions;
using OmniPanel.Themes;
using SkiaSharp;

namespace OmniPanel.Rendering.Widgets;

/// <summary>Draws templated text with shrink-to-fit and width stabilisation.</summary>
internal static class TextWidgetRenderer
{
    private const char FigureSpace = '\u2007';

    public static void Render(TextWidget widget, ThemeRect rect, WidgetRenderContext ctx)
    {
        if (rect.IsEmpty || widget.Opacity <= 0d)
        {
            return;
        }

        if (!TextTemplate.TryParse(widget.Text, out var template, out _))
        {
            ctx.DrawErrorMarker(rect);
            return;
        }

        var text = template!.Render(ctx.Bindings);
        if (widget.StabilizeWidth)
        {
            text = Stabilize(text);
        }

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var family = widget.FontFamily ?? ctx.Defaults.FontFamily;
        var typeface = ctx.Fonts.Resolve(family, widget.FontWeight, widget.Italic);
        var font = ctx.Paints.Font;
        font.Typeface = typeface;
        font.Size = (float)Math.Max(1d, widget.FontSize);
        font.Edging = SKFontEdging.SubpixelAntialias;

        if (widget.ShrinkToFit)
        {
            ShrinkToFit(font, text, rect, widget);
        }

        var color = ResolveTextColor(widget, template, ctx);

        float textWidth = MeasureWidth(font, text, widget.LetterSpacing);
        var metrics = font.Metrics;
        float textHeight = metrics.Descent - metrics.Ascent;

        var x = widget.Align switch
        {
            HorizontalAlignment.Center => (float)(rect.X + ((rect.Width - textWidth) / 2d)),
            HorizontalAlignment.Right => (float)(rect.Right - textWidth),
            _ => (float)rect.X,
        };

        var baseline = widget.VerticalAlign switch
        {
            VerticalAlignment.Middle => (float)(rect.CenterY - ((metrics.Ascent + metrics.Descent) / 2d)),
            VerticalAlignment.Bottom => (float)(rect.Bottom - metrics.Descent),
            VerticalAlignment.Baseline => (float)rect.Bottom,
            _ => (float)(rect.Y - metrics.Ascent),
        };

        _ = textHeight;

        if (widget.StrokeColor is not null && widget.StrokeWidth > 0d)
        {
            var stroke = ctx.Paints.Stroke;
            stroke.Color = ctx.ResolveColor(widget.StrokeColor, null);
            stroke.StrokeWidth = (float)widget.StrokeWidth;
            stroke.Style = SKPaintStyle.Stroke;
            stroke.IsStroke = true;
            DrawText(ctx.Canvas, text, x, baseline, font, stroke, widget.LetterSpacing);
        }

        var fill = ctx.Paints.Fill;
        fill.Color = color;
        fill.Style = SKPaintStyle.Fill;
        fill.IsStroke = false;
        DrawText(ctx.Canvas, text, x, baseline, font, fill, widget.LetterSpacing);
    }

    private static SKColor ResolveTextColor(TextWidget widget, TextTemplate template, WidgetRenderContext ctx)
    {
        if (widget.ColorStops is { Count: > 0 })
        {
            double? number = null;
            if (widget.ColorBinding is { } binding
                && BindingExpression.TryParse(binding, out var expr, out _))
            {
                number = expr!.Evaluate(ctx.Bindings).AsNumber();
            }
            else if (template.PrimarySensor is { } sensor)
            {
                number = ctx.Bindings.ResolveSensor(sensor).AsNumber();
            }

            if (number is { } value)
            {
                var fallback = ctx.Colors.Get(widget.Color, ctx.Defaults.Color, ThemeColor.White).ToTheme();
                return ColorStopResolver.Resolve(widget.ColorStops, value, fallback).ToSk().WithOpacity(ctx.Opacity);
            }
        }

        return ctx.ResolveColor(widget.Color, ctx.Defaults.Color);
    }

    private static void ShrinkToFit(SKFont font, string text, ThemeRect rect, TextWidget widget)
    {
        var size = font.Size;
        while (size > 4f)
        {
            font.Size = size;
            if (MeasureWidth(font, text, widget.LetterSpacing) <= rect.Width
                && (font.Metrics.Descent - font.Metrics.Ascent) <= rect.Height)
            {
                return;
            }

            size -= 0.5f;
        }

        font.Size = Math.Max(4f, size);
    }

    private static float MeasureWidth(SKFont font, string text, double letterSpacing)
    {
        var width = font.MeasureText(text);
        if (letterSpacing != 0d && text.Length > 1)
        {
            width += (float)(letterSpacing * (text.Length - 1));
        }

        return width;
    }

    private static void DrawText(
        SKCanvas canvas,
        string text,
        float x,
        float baseline,
        SKFont font,
        SKPaint paint,
        double letterSpacing)
    {
        if (letterSpacing == 0d)
        {
            canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
            return;
        }

        var cursor = x;
        foreach (var ch in text)
        {
            var s = ch.ToString();
            canvas.DrawText(s, cursor, baseline, SKTextAlign.Left, font, paint);
            cursor += font.MeasureText(s) + (float)letterSpacing;
        }
    }

    /// <summary>
    /// Pads digit runs with figure spaces so "99" and "100" occupy the same
    /// advance width and a centred readout does not jitter.
    /// </summary>
    internal static string Stabilize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Pad each maximal digit run to at least 3 characters — enough for the
        // common 0-100 percentage / temperature case without over-padding clocks.
        var builder = new StringBuilder(text.Length + 4);
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsAsciiDigit(text[i]))
            {
                var start = i;
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }

                var run = text[start..i];
                const int minDigits = 3;
                if (run.Length < minDigits)
                {
                    builder.Append(FigureSpace, minDigits - run.Length);
                }

                builder.Append(run);
            }
            else
            {
                builder.Append(text[i]);
                i++;
            }
        }

        return builder.ToString();
    }
}
