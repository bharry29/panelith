using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Panelith.App.Controls;

/// <summary>A small filled line chart of recent values.</summary>
public sealed class Sparkline : Control
{
    /// <summary>The values to plot, oldest first.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>Line colour.</summary>
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<Sparkline, Color>(nameof(Color), Colors.Cyan);

    /// <summary>Fixed lower bound, or NaN to fit the data.</summary>
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Minimum), double.NaN);

    /// <summary>Fixed upper bound, or NaN to fit the data.</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), double.NaN);

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, ColorProperty, MinimumProperty, MaximumProperty);

    /// <inheritdoc cref="ValuesProperty"/>
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <inheritdoc cref="ColorProperty"/>
    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <inheritdoc cref="MinimumProperty"/>
    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <inheritdoc cref="MaximumProperty"/>
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var values = Values;
        if (values is not { Count: > 1 } || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var min = double.IsNaN(Minimum) ? values.Min() : Minimum;
        var max = double.IsNaN(Maximum) ? values.Max() : Maximum;
        if (max - min < 1e-6)
        {
            max = min + 1;
        }

        var w = Bounds.Width;
        var h = Bounds.Height;
        var points = new Point[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var t = Math.Clamp((values[i] - min) / (max - min), 0, 1);
            points[i] = new Point(w * i / (values.Count - 1), h - (t * (h - 2)) - 1);
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], false);
            for (var i = 1; i < points.Length; i++)
            {
                g.LineTo(points[i]);
            }

            g.EndFigure(false);
        }

        using (var g = area.Open())
        {
            g.BeginFigure(new Point(0, h), true);
            foreach (var p in points)
            {
                g.LineTo(p);
            }

            g.LineTo(new Point(w, h));
            g.EndFigure(true);
        }

        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(90, Color.R, Color.G, Color.B), 0),
                new GradientStop(Color.FromArgb(0, Color.R, Color.G, Color.B), 1),
            },
        };

        context.DrawGeometry(fill, null, area);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
    }
}
