using Avalonia;
using Avalonia.Controls;

namespace Panelith.App.Controls;

/// <summary>
/// Lays children out in rows that wrap. A child stays between
/// <see cref="MinItemWidth"/> and <see cref="MaxItemWidth"/>: a wider window
/// adds columns instead of stretching the ones already there, which is what
/// keeps the dashboard and the screen list usable on an ultrawide monitor.
/// </summary>
public sealed class WrappingGrid : Panel
{
    /// <summary>The narrowest a child may be before the row wraps to fewer columns.</summary>
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<WrappingGrid, double>(nameof(MinItemWidth), 320d);

    /// <summary>The widest a child may grow. Extra space is left empty rather than stretching past this.</summary>
    public static readonly StyledProperty<double> MaxItemWidthProperty =
        AvaloniaProperty.Register<WrappingGrid, double>(nameof(MaxItemWidth), 560d);

    /// <summary>Gap between columns.</summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<WrappingGrid, double>(nameof(ColumnSpacing), 16d);

    /// <summary>Gap between rows.</summary>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<WrappingGrid, double>(nameof(RowSpacing), 16d);

    static WrappingGrid()
    {
        AffectsMeasure<WrappingGrid>(MinItemWidthProperty, MaxItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    /// <inheritdoc cref="MinItemWidthProperty"/>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <inheritdoc cref="MaxItemWidthProperty"/>
    public double MaxItemWidth
    {
        get => GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    /// <inheritdoc cref="ColumnSpacingProperty"/>
    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <inheritdoc cref="RowSpacingProperty"/>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>
    /// How many columns fit, and how wide each one is.
    /// </summary>
    /// <param name="availableWidth">The width the row has.</param>
    /// <param name="count">How many children there are.</param>
    /// <param name="minItemWidth">Narrowest a child should be.</param>
    /// <param name="maxItemWidth">Widest a child may grow.</param>
    /// <param name="spacing">Gap between columns.</param>
    public static (int Columns, double ItemWidth) Plan(double availableWidth, int count, double minItemWidth, double maxItemWidth, double spacing)
    {
        if (count <= 0 || availableWidth <= 0 || double.IsNaN(availableWidth) || double.IsInfinity(availableWidth))
        {
            return (1, 0);
        }

        var gap = Math.Max(0, spacing);
        var min = Math.Max(1, minItemWidth);
        var max = Math.Max(min, maxItemWidth);

        var columns = (int)Math.Floor((availableWidth + gap) / (min + gap));
        columns = Math.Clamp(columns, 1, count);

        var stretched = (availableWidth - (gap * (columns - 1))) / columns;
        if (double.IsNaN(stretched) || stretched < 0)
        {
            stretched = availableWidth;
        }

        // Narrower than one minimum column: use the width there is, so the
        // page scrolls vertically instead of sideways.
        var itemWidth = stretched < min ? stretched : Math.Min(max, stretched);
        return (columns, itemWidth);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MaxItemWidth * Math.Max(1, Children.Count);
        var height = Layout(width, arrange: false);
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private double Layout(double width, bool arrange)
    {
        var count = Children.Count;
        if (count == 0)
        {
            return 0;
        }

        var (columns, itemWidth) = Plan(width, count, MinItemWidth, MaxItemWidth, ColumnSpacing);
        var heights = new double[count];
        for (var i = 0; i < count; i++)
        {
            Children[i].Measure(new Size(Math.Max(0, itemWidth), double.PositiveInfinity));
            heights[i] = Children[i].DesiredSize.Height;
        }

        var y = 0d;
        var rows = (count + columns - 1) / columns;
        for (var row = 0; row < rows; row++)
        {
            var start = row * columns;
            var end = Math.Min(count, start + columns);
            var rowHeight = 0d;
            for (var i = start; i < end; i++)
            {
                rowHeight = Math.Max(rowHeight, heights[i]);
            }

            if (arrange)
            {
                for (var column = 0; column < end - start; column++)
                {
                    Children[start + column].Arrange(new Rect(column * (itemWidth + ColumnSpacing), y, itemWidth, rowHeight));
                }
            }

            y += rowHeight;
            if (row < rows - 1)
            {
                y += RowSpacing;
            }
        }

        return y;
    }
}
